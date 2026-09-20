using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("片刻收纳")]
[assembly: AssemblyDescription("轻量 Windows 桌面文件整理工具")]
[assembly: AssemblyProduct("片刻收纳")]
[assembly: AssemblyCompany("Pivkey")]
[assembly: AssemblyVersion("0.1.1.0")]
[assembly: AssemblyFileVersion("0.1.1.0")]

namespace PivkeyOrganizer
{
    // Wire DTOs for the JS -> C# panel sync. Field names match the JS keys
    // verbatim (camelCase) so JavaScriptSerializer binds without case guessing.
    internal sealed class PanelItemData
    {
        public string id;
        public string name;
        public string path;
        public string iconUrl;
        public string extension;
        public string modifiedAt;
        public double size;
        public string kind;
        public bool readOnly;
        public bool favorite;
        public bool pinned;
    }

    internal sealed class PanelSyncData
    {
        public string id;
        public string name;
        public string color;
        public string themeAccent;
        public string headerSurface;
        public double glassOpacity;
        public string theme;
        public string material;       // 材质模式：acrylic=真亚克力模糊，solid=纯色渐变（缺省 acrylic）
        public bool compact;
        public double uiScale;       // 界面缩放百分比（80–130，缺省 100）；仅影响 DIP 逻辑尺寸，不含系统 DPI
        public double itemSize;
        public double iconSize;
        public double itemGap;
        public double labelSize;
        public string itemAlignment;
        public int itemColumns;
        public bool showLabels;
        public string viewMode;
        public string sortMode;
        public bool collapsed;
        public bool pinned;
        public bool capsuleMode;      // 图标胶囊模式（全局）
        public string categoryIcon;   // 分类图标：Phosphor 图标名 或 data: 图片 URL
        public bool readOnly;         // 门户分区：只读，不允许把外部文件移动进来
        public bool showExtensions;
        public string labelPosition;
        public bool autoHide;
        public int autoHideDelaySeconds;
        public double x;
        public double y;
        public double width;
        public double height;
        public int revision;
        public PanelItemData[] items;
    }

    public sealed class PivkeyApp : Application
    {
        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
        private const int ASFW_ANY = -1;

        private static System.Threading.Mutex singleInstanceMutex;
        private static System.Threading.EventWaitHandle showSettingsEvent;
        private static System.Threading.EventWaitHandle newTodoEvent;
        private static System.Threading.EventWaitHandle newNoteEvent;
        private static DesktopWindow hostWindow;

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            singleInstanceMutex = new System.Threading.Mutex(true, "PivkeyOrganizer_SingleInstance_Mutex", out createdNew);
            try { DesktopWindow.Log("Main 进程启动 PID=" + Process.GetCurrentProcess().Id + ", createdNew=" + createdNew + ", args=" + (args != null ? string.Join(" ", args) : "null")); } catch { }
            if (!createdNew)
            {
                try
                {
                    // 核心关键：新启动的进程拥有前台焦点，在退出前授权已有后台实例可抢占前台置顶
                    AllowSetForegroundWindow(ASFW_ANY);
                    string target = "PivkeyOrganizer_ShowSettings_Event";
                    if (args != null && args.Length > 0)
                    {
                        string arg = args[0].ToLowerInvariant();
                        if (arg.Contains("todo")) target = "PivkeyOrganizer_NewTodo_Event";
                        else if (arg.Contains("note")) target = "PivkeyOrganizer_NewNote_Event";
                    }
                    DesktopWindow.Log("第二实例唤醒已有进程: " + target);
                    using (System.Threading.EventWaitHandle signal = System.Threading.EventWaitHandle.OpenExisting(target))
                    {
                        signal.Set();
                    }
                    DesktopWindow.Log("唤醒信号发送完成");
                }
                catch (Exception ex)
                {
                    try { DesktopWindow.Log("第二实例发送信号失败: " + ex); } catch { }
                }
                return;
            }
            // Some launchers provide SystemRoot but omit the legacy windir alias.
            // .NET Framework WPF reads windir while initializing its font cache.
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
            {
                string systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
                if (!String.IsNullOrWhiteSpace(systemRoot))
                    Environment.SetEnvironmentVariable("windir", systemRoot, EnvironmentVariableTarget.Process);
            }
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            PivkeyApp app = new PivkeyApp();
            // 托盘常驻应用：显式控制退出（ExitApplication），任何窗口关闭都不结束进程。
            // 注意不能依赖 OnMainWindowClose：迁移/设置等临时窗口可能在 MainWindow 未显式
            // 指定时被 WPF 当作 MainWindow，其关闭会连带退出整个应用。
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate(object source, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs exArgs)
            {
                try { DesktopWindow.Log("UI线程未处理异常: " + exArgs.Exception); } catch { }
                exArgs.Handled = true;
            };
            // 稳定性：任何未处理异常（含后台线程）都记录到日志，避免静默退出无从排查
            AppDomain.CurrentDomain.UnhandledException += delegate(object source, UnhandledExceptionEventArgs exArgs)
            {
                try { DesktopWindow.Log("未处理异常: " + (exArgs.ExceptionObject as Exception)); } catch { }
            };
            AppDomain.CurrentDomain.ProcessExit += delegate(object source, EventArgs e)
            {
                try { DesktopWindow.Log("ProcessExit 进程即将退出:\n" + Environment.StackTrace); } catch { }
            };
            app.Exit += delegate(object source, ExitEventArgs e)
            {
                try { DesktopWindow.Log("app.Exit 触发 (ExitCode=" + e.ApplicationExitCode + "):\n" + Environment.StackTrace); } catch { }
            };
            TaskScheduler.UnobservedTaskException += delegate(object source, UnobservedTaskExceptionEventArgs e)
            {
                try { DesktopWindow.Log("未观察到的 Task 异常: " + e.Exception); } catch { }
                e.SetObserved();
            };
            hostWindow = new DesktopWindow();
            DesktopWindow window = hostWindow;
            try
            {
                showSettingsEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "PivkeyOrganizer_ShowSettings_Event");
                System.Threading.ThreadPool.RegisterWaitForSingleObject(showSettingsEvent, delegate(object state, bool timedOut)
                {
                    if (timedOut) return;
                    app.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        window.OnSingleInstanceActivated();
                    }));
                }, null, -1, false);

                newTodoEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "PivkeyOrganizer_NewTodo_Event");
                System.Threading.ThreadPool.RegisterWaitForSingleObject(newTodoEvent, delegate(object state, bool timedOut)
                {
                    if (timedOut) return;
                    app.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        window.OnCreateNoteFromContextMenu("todo");
                    }));
                }, null, -1, false);

                newNoteEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "PivkeyOrganizer_NewNote_Event");
                System.Threading.ThreadPool.RegisterWaitForSingleObject(newNoteEvent, delegate(object state, bool timedOut)
                {
                    if (timedOut) return;
                    app.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        window.OnCreateNoteFromContextMenu("note");
                    }));
                }, null, -1, false);
            }
            catch { }
            // 桌面宿主窗口不再承载 webview、也不再显示（面板窗口各自独立显示）：
            // 先访问 Handle 强制创建窗口源（触发 OnSourceInitialized → 注册全局热键），
            // 宿主窗口不显示，但必须创建句柄（OnSourceInitialized 里注册全局热键）。
            // 实测 WindowInteropHelper.Handle 对从未 Show 的窗口返回 Zero（WPF 不创建句柄），
            // 因此用 Show→Hide：Show 触发句柄创建与 OnSourceInitialized（其内部已把窗口
            // 区域清空为空 Region，窗口即使显示也完全不可见、不拦截桌面点击），随后立即隐藏。
            window.Show();
            window.Hide();
            bool isBackgroundStart = false;
            if (args != null && args.Length > 0)
            {
                string firstArg = args[0].ToLowerInvariant();
                if (firstArg.Contains("todo")) window.OnCreateNoteFromContextMenu("todo");
                else if (firstArg.Contains("note")) window.OnCreateNoteFromContextMenu("note");
                else if (firstArg.Contains("background") || firstArg.Contains("autostart") || firstArg.Contains("silent")) isBackgroundStart = true;
            }
            // 用户直接手动双击 PivkeyOrganizer.exe 启动时，主动展示设置管理窗口并浮起面板，
            // 避免托盘静默常驻导致用户误以为“双击之后无法正常启动打开”
            if (!isBackgroundStart)
            {
                window.ShowSettingsWindow();
            }
            app.Run();
            GC.KeepAlive(singleInstanceMutex);
            GC.KeepAlive(hostWindow);
        }
    }

    public sealed class DesktopWindow : Window, IManagerHost
    {
        private const int GwlExStyle = -20;
        private const int GwlHwndParent = -8;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExTransparent = 0x00000020L;
        private const int WmHotKey = 0x0312;
        private const int WmSysCommand = 0x0112;
        private const int ScMinimize = 0xF020;
        private const int HotKeyId = 0x5159;
        private const int SettingsHotKeyId = 0x515A;
        private const int SearchHotKeyId = 0x515B;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint VkQ = 0x51;
        private const uint VkS = 0x53;
        private const uint VkF = 0x46;
        private const int RgnOr = 2;

        private readonly Manager manager;   // 原生核心：配置/扫描/收纳/面板同步全部由它驱动
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly Forms.NotifyIcon trayIcon;
        private System.Drawing.Icon trayIconAsset;
        private Forms.ContextMenuStrip trayMenu;
        private PivkeyMenuPalette trayMenuPalette;
        private string trayMenuTheme;
        private string trayMenuAccent;
        private readonly Dictionary<string, PanelWindow> panelWindows = new Dictionary<string, PanelWindow>();
        private readonly Dictionary<string, NoteWindow> noteWindows = new Dictionary<string, NoteWindow>();
        private readonly Dictionary<string, IconCacheItem> shellIconCache = new Dictionary<string, IconCacheItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ScanCacheItem> scanEntryCache = new Dictionary<string, ScanCacheItem>(StringComparer.OrdinalIgnoreCase);
        private readonly object shellIconCacheLock = new object();
        private readonly List<FileSystemWatcher> fileWatchers = new List<FileSystemWatcher>();
        private readonly List<FileSystemWatcher> portalWatchers = new List<FileSystemWatcher>();
        private readonly Dictionary<string, string> panelNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> panelReadOnly = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer desktopChangeDebounce;
        private bool startupSyncDone; // 首次面板同步是否已完成（只触发一次启动期托管堆压缩）
        private string watchedDesktopPath;
        private const int MaxIconCacheEntries = 400;
        private SettingsWindow settingsWindow;
        private CoreWebView2Environment webEnvironment;
        private Task<bool> webEnvironmentTask;      // 懒创建 WebView2 环境的去重任务
        private string webRoot;
        private IntPtr hwnd;
        private bool clickThrough;
        private bool closed;                        // OnClosed 防重入
        private bool managerStarted;                // manager.Initialize 是否已调用
        // 一次性启动迁移 webview（config.json 缺失时创建，迁移完成/超时后销毁）
        private WebView2CompositionControl migrationWebView;
        private System.Windows.Window migrationWindow;   // 承载迁移 webview 的隐藏窗口（无视觉树时 EnsureCoreWebView2Async 会挂起）
        private DispatcherTimer migrationTimeoutTimer;
        private DispatcherTimer autoHideTimer;
        private QuickSearchWindow quickSearchWindow;
        private uint taskbarCreatedMessage;   // Explorer 重启广播（TaskbarCreated）：重建桌面层挂载
        internal static readonly uint WmShowSettings = RegisterWindowMessage("PivkeyOrganizer_ShowSettings");

        public DesktopWindow()
        {
            Title = "片刻收纳";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Left = SystemParameters.WorkArea.Left;
            Top = SystemParameters.WorkArea.Top;
            Width = SystemParameters.WorkArea.Width;
            Height = SystemParameters.WorkArea.Height;

            // 管理器 webview 已移除：桌面窗口不再承载任何内容，仅保留 hwnd（热键需要）。
            // 窗口从不 Show()——它曾是 Progman 下的透明层，现在面板窗口各自独立显示。
            manager = new Manager(this);
            trayIcon = CreateTrayIcon();
            autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            autoHideTimer.Tick += delegate
            {
                if (closed) return;
                try
                {
                    System.Drawing.Point cursor = Forms.Cursor.Position;
                    foreach (PanelWindow panel in new List<PanelWindow>(panelWindows.Values))
                        panel.AutoHideTick(cursor.X, cursor.Y);
                }
                catch { }
            };
            autoHideTimer.Start();
            SourceInitialized += OnSourceInitialized;
            Closed += OnClosed;
            StateChanged += OnStateChanged;
            WarmUpDesktopScan();
            // 窗口从不 Show()，Loaded 不会触发：启动流程直接在这里执行。
            try
            {
                if (File.Exists(GetConfigPath())) StartManager();   // 常规启动：直接初始化管理器
                else EnsureConfigMigrated();   // 首次启动：先尝试迁移旧配置（完成后内部启动管理器）
                Log("片刻收纳宿主初始化完成");
            }
            catch (Exception error)
            {
                Log("宿主启动失败：" + error.Message);
            }
        }

        // 提前在后台线程扫描桌面与受管收纳目录并生成图标缓存，让 WebView2
        // 初始化与首屏同步与图标提取重叠进行，从而显著缩短第一次真实扫描的耗时。
        private void WarmUpDesktopScan()
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (String.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath)) return;
            Task.Run(delegate
            {
                try
                {
                    WarmUpDirectory(desktopPath);
                    foreach (string name in new string[] { "片刻收纳", "轻屿收纳" })
                    {
                        string root = Path.Combine(desktopPath, name);
                        if (!Directory.Exists(root)) continue;
                        foreach (string folder in Directory.GetDirectories(root)) WarmUpDirectory(folder);
                    }
                }
                catch { }
            });
        }

        private void WarmUpDirectory(string path)
        {
            string[] directories = Directory.GetDirectories(path);
            foreach (string directory in directories) { try { ScannedEntry(directory, "DIRECTORY", false); } catch { } }
            string[] files = Directory.GetFiles(path);
            foreach (string file in files) { try { ScannedEntry(file, "FILE", false); } catch { } }
        }

        private void OnSourceInitialized(object sender, EventArgs args)
        {
            WindowInteropHelper helper = new WindowInteropHelper(this);
            hwnd = helper.Handle;
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null)
            {
                source.AddHook(WndProc);
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExToolWindow));
            // 桌面层挂载（DeskBox 策略，WidgetLayer 带回读验证）：优先 DefView，桌面未就绪
            // 回退 Progman 并自动升级
            WidgetLayer.Attach(hwnd);
            // 唤起/回落会话初始化：交互深度联动 Manager.interactionActive（暂停自动收纳）
            LayerSession.Initialize(delegate(bool active) { manager.SetInteractionActive(active); });
            // Explorer 重启广播：挂载缓存失效后重建（见 WndProc）
            taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
            // 宿主窗口不显示：清空窗口区域作为兜底，即使被意外 Show 也不会拦截桌面点击
            SetWindowRgn(hwnd, CreateRectRgn(0, 0, 0, 0), true);

            RegisterHotKey(hwnd, HotKeyId, ModControl | ModAlt, VkQ);
            RegisterHotKey(hwnd, SettingsHotKeyId, ModControl | ModAlt, VkS);
            RegisterHotKey(hwnd, SearchHotKeyId, ModControl | ModAlt, VkF);
        }

        // 初始化管理器（幂等，只执行一次）
        private void StartManager()
        {
            if (managerStarted) return;
            managerStarted = true;
            manager.Initialize();
            // 预热设置窗口：2秒空闲后在后台提前拉起 WebView2 环境并预载设置页面，实现毫秒级秒开
            DispatcherTimer warmUpTimer = new DispatcherTimer(DispatcherPriority.Background);
            warmUpTimer.Interval = TimeSpan.FromSeconds(2);
            warmUpTimer.Tick += delegate
            {
                warmUpTimer.Stop();
                PrewarmSettingsWindow();
            };
            warmUpTimer.Start();
        }

        private async void PrewarmSettingsWindow()
        {
            if (closed) return;
            try
            {
                // 后台预热 WebView2 运行环境，避免首次打开设置时的 Edge 引擎冷启动耗时
                await EnsureWebEnvironment();
            }
            catch { }
        }

        private void OnClosed(object sender, EventArgs args)
        {
            if (closed) return;
            closed = true;
            DestroyMigrationWebView(false);   // 关闭迁移 webview（不启动管理器）
            if (settingsWindow != null)
            {
                SettingsWindow closingSettings = settingsWindow;
                settingsWindow = null;
                closingSettings.PrepareExit();
                closingSettings.Close();
            }
            foreach (PanelWindow panel in new List<PanelWindow>(panelWindows.Values)) panel.Close();
            panelWindows.Clear();
            foreach (NoteWindow note in new List<NoteWindow>(noteWindows.Values))
            {
                if (note != null) try { note.Close(); } catch { }
            }
            noteWindows.Clear();
            DisposeDesktopWatchers();
            DisposePortalWatchers();
            if (desktopChangeDebounce != null)
            {
                desktopChangeDebounce.Stop();
                desktopChangeDebounce = null;
            }
            manager.Dispose();   // 停止全部定时器并立即写盘
            if (autoHideTimer != null) { autoHideTimer.Stop(); autoHideTimer = null; }
            if (quickSearchWindow != null)
            {
                try { quickSearchWindow.Close(); } catch { }
                quickSearchWindow = null;
            }
            if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, HotKeyId);
            if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, SettingsHotKeyId);
            if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, SearchHotKeyId);
            trayIcon.Visible = false;
            trayIcon.Dispose();
            if (trayIconAsset != null) { trayIconAsset.Dispose(); trayIconAsset = null; }
        }

        private static string PrepareWebViewDataPath()
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = Path.Combine(localData, "PivkeyOrganizer");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "WebView2");
        }

        private Forms.NotifyIcon CreateTrayIcon()
        {
            trayMenuTheme = "light";
            trayMenuAccent = "#3478f6";
            trayMenuPalette = PivkeyMenuPalette.Create("light", "#3478f6");
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            trayMenu = menu;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.AutoSize = true;
            menu.MinimumSize = new System.Drawing.Size(186, 0);
            menu.MaximumSize = new System.Drawing.Size(220, 0);
            menu.Padding = new Forms.Padding(0, 4, 0, 4);
            menu.BackColor = trayMenuPalette.Surface;
            menu.ForeColor = trayMenuPalette.Text;
            menu.Font = CreateTrayFont(9.5F, System.Drawing.FontStyle.Regular);
            menu.DropShadowEnabled = true;
            menu.ShowItemToolTips = false;
            menu.Renderer = new PivkeyMenuRenderer(trayMenuPalette);
            menu.Opened += delegate
            {
                ApplyTrayMenuRegion(menu);
            };

            // 1. 整理桌面...（对齐 DeskBox 菜单布局）
            AddDeskBoxTrayItem(menu, "整理桌面...", "grid", delegate { manager.OrganizeNow(false); });
            AddDeskBoxTraySeparator(menu);

            // 2. 新建格子
            AddDeskBoxTrayItem(menu, "新建格子", "document", delegate { OpenCategoryDialog(); });

            // 3. 新建待办清单 & 随手备忘
            AddDeskBoxTrayItem(menu, "新建待办清单", "check", delegate { manager.CreateNoteWithMode("todo", null); });
            AddDeskBoxTrayItem(menu, "新建随手备忘", "note", delegate { manager.CreateNoteWithMode("note", null); });

            // 4. 新建文件夹映射
            AddDeskBoxTrayItem(menu, "新建文件夹映射", "folder-mapping", delegate { CreateFolderMappingFromPicker(); });

            // 5. 添加功能格子
            AddDeskBoxTrayItem(menu, "添加功能格子", "plus", delegate { AddFeatureWidgetFromTray(); });
            AddDeskBoxTraySeparator(menu);

            // 5. 打开收纳目录
            AddDeskBoxTrayItem(menu, "打开收纳目录", "folder", delegate { OpenManagedStorageDirectory(); });
            AddDeskBoxTraySeparator(menu);

            // 6. 设置
            AddDeskBoxTrayItem(menu, "设置", "settings", delegate { ShowSettingsWindow(); });
            AddDeskBoxTraySeparator(menu);

            // 7. 退出
            AddDeskBoxTrayItem(menu, "退出", "exit", delegate { ExitApplication(); });

            Forms.NotifyIcon icon = new Forms.NotifyIcon();
            trayIconAsset = LoadApplicationIcon();
            icon.Icon = trayIconAsset ?? System.Drawing.SystemIcons.Application;
            icon.Text = "片刻收纳";
            icon.ContextMenuStrip = menu;

            // 左键单击：切换全部分区显示/隐藏（对齐 DeskBox 托盘左键行为）
            icon.MouseClick += (sender, args) =>
            {
                if (args.Button == Forms.MouseButtons.Left)
                {
                    OnTray(delegate { TogglePanelsVisibility(); });
                }
            };

            // 双击：打开设置
            icon.DoubleClick += (sender, args) => OnTray(delegate { ShowSettingsWindow(); });

            icon.Visible = true;
            ApplyTrayMenuPalette(menu, trayMenuPalette);
            return icon;
        }

        private static System.Drawing.Icon LoadApplicationIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "pivkey-organizer.ico");
                if (File.Exists(iconPath)) return new System.Drawing.Icon(iconPath);
            }
            catch { }
            return null;
        }

        private Forms.ToolStripMenuItem AddDeskBoxTrayItem(Forms.ContextMenuStrip menu, string text, string iconName, Action action)
        {
            Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem(text);
            item.AutoSize = false;
            item.Size = new System.Drawing.Size(186, 36);
            item.Padding = new Forms.Padding(0);
            item.Margin = new Forms.Padding(0);
            item.Tag = "deskbox|" + iconName;
            item.ToolTipText = text;
            item.Click += delegate { OnTray(action); };
            menu.Items.Add(item);
            return item;
        }

        private static Forms.ToolStripSeparator AddDeskBoxTraySeparator(Forms.ContextMenuStrip menu)
        {
            Forms.ToolStripSeparator separator = new Forms.ToolStripSeparator();
            separator.AutoSize = false;
            separator.Size = new System.Drawing.Size(186, 7);
            separator.Padding = new Forms.Padding(0);
            separator.Margin = new Forms.Padding(0);
            menu.Items.Add(separator);
            return separator;
        }

        private void TogglePanelsVisibility()
        {
            bool anyVisible = false;
            foreach (PanelWindow panel in panelWindows.Values)
            {
                if (panel.IsVisible) { anyVisible = true; break; }
            }
            SetPanelsVisible(!anyVisible);
        }

        private void AddFeatureWidgetFromTray()
        {
            OpenCategoryDialog();
        }

        private static void OpenManagedStorageDirectory()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string root = Path.Combine(desktop, "片刻收纳");
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                Process.Start("explorer.exe", root);
            }
            catch { }
        }

        private void CreateFolderMappingFromPicker()
        {
            try
            {
                string path = PickFolderCore();
                if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
                string folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (String.IsNullOrWhiteSpace(folderName)) folderName = path;

                List<string> existingNames = new List<string>();
                Dictionary<string, object> config = null;
                try
                {
                    object parsed = serializer.DeserializeObject(manager.SerializeConfig());
                    config = parsed as Dictionary<string, object>;
                }
                catch { }
                if (config == null) config = new Dictionary<string, object>();
                Dictionary<string, object> prefs = config.ContainsKey("preferences") ? config["preferences"] as Dictionary<string, object> : null;
                if (prefs == null)
                {
                    prefs = new Dictionary<string, object>();
                    config["preferences"] = prefs;
                }
                object[] rawCategories = prefs.ContainsKey("categories") ? prefs["categories"] as object[] : null;
                if (rawCategories != null)
                {
                    foreach (object raw in rawCategories)
                    {
                        Dictionary<string, object> entry = raw as Dictionary<string, object>;
                        if (entry == null) continue;
                        object name;
                        if (entry.TryGetValue("name", out name) && name != null) existingNames.Add(Convert.ToString(name));
                    }
                }

                string uniqueName = folderName;
                int suffix = 1;
                while (existingNames.Contains(uniqueName))
                {
                    uniqueName = folderName + " (" + suffix + ")";
                    suffix++;
                }

                List<object> next = new List<object>();
                if (rawCategories != null) foreach (object raw in rawCategories) next.Add(raw);
                Dictionary<string, object> categoryEntry = new Dictionary<string, object>();
                categoryEntry["id"] = Guid.NewGuid().ToString("N");
                categoryEntry["name"] = uniqueName;
                categoryEntry["icon"] = "folder";
                categoryEntry["color"] = "#3478f6";
                categoryEntry["extensions"] = "*";
                categoryEntry["acceptsFolders"] = true;
                next.Add(categoryEntry);
                prefs["categories"] = next.ToArray();
                manager.ApplyConfig(config);
                Log("已创建文件夹映射：" + uniqueName);
            }
            catch (Exception ex)
            {
                Log("新建文件夹映射失败：" + ex.Message);
            }
        }

        private string currentTheme = "light";
        private string currentAccent = "#3478f6";
        private string currentHeaderSurface = null;
        private int currentGlassOpacity = 88;
        private string currentMaterial = "acrylic";
        private double currentUiScale = 100;   // 最近一次同步到的界面缩放（80–130）；仅影响 DIP 逻辑尺寸

        // 界面缩放变化时失效 shell 图标缓存：下一次扫描会按新的像素档位重新生成图标。
        // 只做缓存失效，不触发重扫——重扫由 Manager 的既有流程负责。
        private void ApplyUiScale(double uiScale)
        {
            double next = (uiScale >= 80 && uiScale <= 130) ? uiScale : 100;
            if (Math.Abs(next - currentUiScale) < 0.5) return;
            currentUiScale = next;
            InvalidateShellIconCache();
        }

        // 当前显示器的 DPI 缩放比（96 DPI = 1.0）。句柄未就绪时回退主屏 DPI。
        internal double CurrentDpiScale()
        {
            try
            {
                IntPtr handle = hwnd;
                if (handle != IntPtr.Zero)
                {
                    uint dpi = GetDpiForWindow(handle);
                    if (dpi > 0) return dpi / 96.0;
                }
            }
            catch { }
            try
            {
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    if (g != null && g.DpiX > 0) return g.DpiX / 96.0;
                }
            }
            catch { }
            return 1.0;
        }

        private void ApplyTrayMenuPalette(PanelSyncData panel)
        {
            if (panel == null) return;
            string theme = String.IsNullOrWhiteSpace(panel.theme) ? "light" : panel.theme;
            string accent = String.IsNullOrWhiteSpace(panel.themeAccent) ? "#3478f6" : panel.themeAccent;
            int glass = (int)panel.glassOpacity;
            string material = String.IsNullOrWhiteSpace(panel.material) ? "acrylic" : panel.material;
            string surface = panel.headerSurface;

            currentTheme = theme;
            currentAccent = accent;
            currentHeaderSurface = surface;
            currentGlassOpacity = glass;
            currentMaterial = material;

            if (!String.Equals(trayMenuTheme, theme, StringComparison.OrdinalIgnoreCase)
                || !String.Equals(trayMenuAccent, accent, StringComparison.OrdinalIgnoreCase))
            {
                trayMenuTheme = theme;
                trayMenuAccent = accent;
                ApplyTrayMenuPalette(trayMenu, PivkeyMenuPalette.Create(theme, accent));
            }

            foreach (NoteWindow noteWin in noteWindows.Values)
            {
                if (noteWin != null)
                {
                    noteWin.ApplyThemeVisuals(currentTheme, currentAccent, currentHeaderSurface, currentGlassOpacity, currentMaterial);
                }
            }
        }

        private void ApplyTrayMenuPalette(Forms.ContextMenuStrip menu, PivkeyMenuPalette palette)
        {
            if (menu == null || palette == null) return;
            trayMenuPalette = palette;
            menu.BackColor = palette.Surface;
            menu.ForeColor = palette.Text;
            menu.Font = CreateTrayFont(9.5F, System.Drawing.FontStyle.Regular);
            menu.Renderer = new PivkeyMenuRenderer(palette);
            menu.Invalidate();
        }

        private static System.Drawing.Font CreateTrayFont(float size, System.Drawing.FontStyle style)
        {
            return FontResources.CreateTrayFont(size, style);
        }

        private static void ApplyTrayMenuRegion(Forms.ContextMenuStrip menu)
        {
            if (menu == null || !menu.IsHandleCreated || menu.Width <= 0 || menu.Height <= 0) return;
            try
            {
                // 优先启用 Win11 DWM 硬件级平滑抗锯齿圆角与立体软阴影，避免 GDI 1-bit 二值裁剪产生的狗牙黑斑
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch { }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(System.Drawing.Rectangle bounds, int radius)
        {
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            int diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(2, radius * 2));
            System.Drawing.Rectangle arc = new System.Drawing.Rectangle(bounds.X, bounds.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawRoundedRectangle(System.Drawing.Graphics g, System.Drawing.Pen pen, float x, float y, float width, float height, float radius)
        {
            using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                float d = radius * 2;
                path.AddArc(x, y, d, d, 180, 90);
                path.AddArc(x + width - d, y, d, d, 270, 90);
                path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
                path.AddArc(x, y + height - d, d, d, 90, 90);
                path.CloseFigure();
                g.DrawPath(pen, path);
            }
        }

        private static void DrawDeskBoxVectorIcon(System.Drawing.Graphics g, string name, System.Drawing.Color color, System.Drawing.Rectangle rect)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Pen pen = new System.Drawing.Pen(color, 1.35f))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

                int x = rect.X;
                int y = rect.Y;

                if (name == "grid")
                {
                    // 整理桌面...：4 宫格四块独立小方格
                    DrawRoundedRectangle(g, pen, x + 1f, y + 1f, 6f, 6f, 1.2f);
                    DrawRoundedRectangle(g, pen, x + 9f, y + 1f, 6f, 6f, 1.2f);
                    DrawRoundedRectangle(g, pen, x + 1f, y + 9f, 6f, 6f, 1.2f);
                    DrawRoundedRectangle(g, pen, x + 9f, y + 9f, 6f, 6f, 1.2f);
                }
                else if (name == "document")
                {
                    // 新建格子：折角文件单页
                    System.Drawing.PointF[] page = new System.Drawing.PointF[]
                    {
                        new System.Drawing.PointF(x + 2.5f, y + 1f),
                        new System.Drawing.PointF(x + 9.5f, y + 1f),
                        new System.Drawing.PointF(x + 13.5f, y + 5f),
                        new System.Drawing.PointF(x + 13.5f, y + 15f),
                        new System.Drawing.PointF(x + 2.5f, y + 15f),
                        new System.Drawing.PointF(x + 2.5f, y + 1f)
                    };
                    g.DrawPolygon(pen, page);
                    g.DrawLine(pen, x + 9.5f, y + 1f, x + 9.5f, y + 5f);
                    g.DrawLine(pen, x + 9.5f, y + 5f, x + 13.5f, y + 5f);
                }
                else if (name == "folder-mapping")
                {
                    // 新建文件夹映射：单页文件 + 左下角快捷方式箭头圆圈
                    System.Drawing.PointF[] page = new System.Drawing.PointF[]
                    {
                        new System.Drawing.PointF(x + 3.5f, y + 1f),
                        new System.Drawing.PointF(x + 10.5f, y + 1f),
                        new System.Drawing.PointF(x + 14.5f, y + 5f),
                        new System.Drawing.PointF(x + 14.5f, y + 15f),
                        new System.Drawing.PointF(x + 6f, y + 15f)
                    };
                    g.DrawLines(pen, page);
                    g.DrawLine(pen, x + 3.5f, y + 1f, x + 3.5f, y + 8f);
                    g.DrawLine(pen, x + 10.5f, y + 1f, x + 10.5f, y + 5f);
                    g.DrawLine(pen, x + 10.5f, y + 5f, x + 14.5f, y + 5f);
                    using (System.Drawing.SolidBrush brush = new System.Drawing.SolidBrush(color))
                    {
                        g.FillEllipse(brush, x + 0.5f, y + 7.5f, 7.5f, 7.5f);
                    }
                    using (System.Drawing.Pen whitePen = new System.Drawing.Pen(System.Drawing.Color.White, 1.1f))
                    {
                        whitePen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        whitePen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawLine(whitePen, x + 2.2f, y + 12.8f, x + 6.2f, y + 8.8f);
                        g.DrawLine(whitePen, x + 3.8f, y + 8.8f, x + 6.2f, y + 8.8f);
                        g.DrawLine(whitePen, x + 6.2f, y + 8.8f, x + 6.2f, y + 11.2f);
                    }
                }
                else if (name == "plus")
                {
                    // 添加功能格子：圆角十字 +
                    g.DrawLine(pen, x + 2.5f, y + 8f, x + 13.5f, y + 8f);
                    g.DrawLine(pen, x + 8f, y + 2.5f, x + 8f, y + 13.5f);
                }
                else if (name == "check" || name == "todo")
                {
                    // 新建待办清单：圆角方框 + 勾选
                    DrawRoundedRectangle(g, pen, x + 1.5f, y + 2f, 13f, 12f, 2f);
                    using (System.Drawing.Pen checkPen = new System.Drawing.Pen(color, 1.4f))
                    {
                        checkPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        checkPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        checkPen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        g.DrawLine(checkPen, x + 4.5f, y + 8f, x + 7f, y + 10.5f);
                        g.DrawLine(checkPen, x + 7f, y + 10.5f, x + 11.5f, y + 5.5f);
                    }
                }
                else if (name == "note")
                {
                    // 新建随手备忘：圆角便签纸 + 横向文本行
                    DrawRoundedRectangle(g, pen, x + 2f, y + 2f, 12f, 12f, 1.8f);
                    g.DrawLine(pen, x + 4.5f, y + 5.5f, x + 11.5f, y + 5.5f);
                    g.DrawLine(pen, x + 4.5f, y + 8.5f, x + 11.5f, y + 8.5f);
                    g.DrawLine(pen, x + 4.5f, y + 11.5f, x + 8.5f, y + 11.5f);
                }
                else if (name == "folder")
                {
                    // 打开收纳目录：文件夹
                    System.Drawing.PointF[] folder = new System.Drawing.PointF[]
                    {
                        new System.Drawing.PointF(x + 1.5f, y + 4.5f),
                        new System.Drawing.PointF(x + 5.5f, y + 4.5f),
                        new System.Drawing.PointF(x + 7.5f, y + 6.5f),
                        new System.Drawing.PointF(x + 14.5f, y + 6.5f),
                        new System.Drawing.PointF(x + 14.5f, y + 14f),
                        new System.Drawing.PointF(x + 1.5f, y + 14f),
                        new System.Drawing.PointF(x + 1.5f, y + 4.5f)
                    };
                    g.DrawPolygon(pen, folder);
                }
                else if (name == "settings")
                {
                    // 设置：齿轮
                    float cx = x + 8f;
                    float cy = y + 8f;
                    float rInner = 2.4f;
                    g.DrawEllipse(pen, cx - rInner, cy - rInner, rInner * 2, rInner * 2);

                    System.Drawing.Drawing2D.GraphicsPath gearPath = new System.Drawing.Drawing2D.GraphicsPath();
                    int teeth = 6;
                    for (int i = 0; i < teeth; i++)
                    {
                        double angle1 = (i * 60 - 15) * Math.PI / 180.0;
                        double angle2 = (i * 60 - 10) * Math.PI / 180.0;
                        double angle3 = (i * 60 + 10) * Math.PI / 180.0;
                        double angle4 = (i * 60 + 15) * Math.PI / 180.0;
                        float rBase = 5.2f;
                        float rTip = 7.0f;
                        System.Drawing.PointF p1 = new System.Drawing.PointF((float)(cx + rBase * Math.Cos(angle1)), (float)(cy + rBase * Math.Sin(angle1)));
                        System.Drawing.PointF p2 = new System.Drawing.PointF((float)(cx + rTip * Math.Cos(angle2)), (float)(cy + rTip * Math.Sin(angle2)));
                        System.Drawing.PointF p3 = new System.Drawing.PointF((float)(cx + rTip * Math.Cos(angle3)), (float)(cy + rTip * Math.Sin(angle3)));
                        System.Drawing.PointF p4 = new System.Drawing.PointF((float)(cx + rBase * Math.Cos(angle4)), (float)(cy + rBase * Math.Sin(angle4)));
                        if (i == 0) gearPath.StartFigure();
                        gearPath.AddLine(p1, p2);
                        gearPath.AddLine(p2, p3);
                        gearPath.AddLine(p3, p4);
                    }
                    gearPath.CloseFigure();
                    g.DrawPath(pen, gearPath);
                }
                else if (name == "exit")
                {
                    // 退出：✕
                    g.DrawLine(pen, x + 3.5f, y + 3.5f, x + 12.5f, y + 12.5f);
                    g.DrawLine(pen, x + 12.5f, y + 3.5f, x + 3.5f, y + 12.5f);
                }
            }
        }

        private sealed class PivkeyMenuPalette
        {
            public readonly bool Dark;
            public readonly System.Drawing.Color Surface;
            public readonly System.Drawing.Color Text;
            public readonly System.Drawing.Color Icon;
            public readonly System.Drawing.Color Hover;
            public readonly System.Drawing.Color Border;
            public readonly System.Drawing.Color Divider;

            private PivkeyMenuPalette(
                bool dark,
                System.Drawing.Color surface,
                System.Drawing.Color text,
                System.Drawing.Color icon,
                System.Drawing.Color hover,
                System.Drawing.Color border,
                System.Drawing.Color divider)
            {
                Dark = dark;
                Surface = surface;
                Text = text;
                Icon = icon;
                Hover = hover;
                Border = border;
                Divider = divider;
            }

            public static PivkeyMenuPalette Create(string theme, string accentValue)
            {
                bool dark = String.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
                System.Drawing.Color surface = dark
                    ? System.Drawing.Color.FromArgb(246, 44, 44, 44)
                    : System.Drawing.Color.FromArgb(250, 252, 252, 252);
                System.Drawing.Color text = dark
                    ? System.Drawing.Color.FromArgb(245, 245, 245)
                    : System.Drawing.Color.FromArgb(26, 26, 26);
                System.Drawing.Color icon = dark
                    ? System.Drawing.Color.FromArgb(210, 210, 210)
                    : System.Drawing.Color.FromArgb(38, 38, 38);
                System.Drawing.Color hover = dark
                    ? System.Drawing.Color.FromArgb(22, 255, 255, 255)
                    : System.Drawing.Color.FromArgb(14, 0, 0, 0);
                System.Drawing.Color border = dark
                    ? System.Drawing.Color.FromArgb(34, 255, 255, 255)
                    : System.Drawing.Color.FromArgb(22, 0, 0, 0);
                System.Drawing.Color divider = dark
                    ? System.Drawing.Color.FromArgb(20, 255, 255, 255)
                    : System.Drawing.Color.FromArgb(16, 0, 0, 0);
                return new PivkeyMenuPalette(dark, surface, text, icon, hover, border, divider);
            }
        }

        private sealed class PivkeyMenuRenderer : Forms.ToolStripRenderer
        {
            private readonly PivkeyMenuPalette palette;

            public PivkeyMenuRenderer(PivkeyMenuPalette value)
            {
                palette = value;
            }

            protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs args)
            {
                using (System.Drawing.SolidBrush brush = new System.Drawing.SolidBrush(palette.Surface))
                {
                    args.Graphics.FillRectangle(brush, args.AffectedBounds);
                }
            }

            protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs args)
            {
                System.Drawing.Rectangle bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, args.ToolStrip.Width - 1), Math.Max(1, args.ToolStrip.Height - 1));
                args.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Pen pen = new System.Drawing.Pen(palette.Border))
                {
                    args.Graphics.DrawRectangle(pen, bounds);
                }
            }

            protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs args)
            {
                if (!(args.Item is Forms.ToolStripMenuItem)) return;
                if (!args.Item.Selected || !args.Item.Enabled) return;

                // Win11 悬停胶囊：左右各内缩 3px，上下各内缩 1px，4px 圆角
                System.Drawing.Rectangle bounds = new System.Drawing.Rectangle(3, 1, Math.Max(1, args.Item.Width - 6), Math.Max(1, args.Item.Height - 2));
                args.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Drawing2D.GraphicsPath path = RoundedRectangle(bounds, 4))
                using (System.Drawing.SolidBrush brush = new System.Drawing.SolidBrush(palette.Hover))
                {
                    args.Graphics.FillPath(brush, path);
                }
            }

            protected override void OnRenderItemImage(Forms.ToolStripItemImageRenderEventArgs args)
            {
                // 由 OnRenderItemText 统一绘制矢量图标与对齐文本
            }

            protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs args)
            {
                string metadata = args.Item.Tag as string;
                string iconName = null;
                if (!String.IsNullOrWhiteSpace(metadata) && metadata.StartsWith("deskbox|"))
                {
                    iconName = metadata.Substring(8);
                }

                // 1. 绘制左侧 16x16 矢量图标 (x = 12, 垂直居中)
                if (!String.IsNullOrWhiteSpace(iconName))
                {
                    int iconY = (args.Item.Height - 16) / 2;
                    System.Drawing.Rectangle iconRect = new System.Drawing.Rectangle(12, iconY, 16, 16);
                    DrawDeskBoxVectorIcon(args.Graphics, iconName, palette.Icon, iconRect);
                }

                // 2. 绘制右侧文字 (x = 38, 垂直居中)
                System.Drawing.Rectangle textRect = new System.Drawing.Rectangle(38, 0, args.Item.Width - 44, args.Item.Height);
                Forms.TextFormatFlags flags = Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine;
                Forms.TextRenderer.DrawText(args.Graphics, args.Text, args.TextFont, textRect, palette.Text, flags);
            }

            protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs args)
            {
                int y = args.Item.Height / 2;
                using (System.Drawing.Pen pen = new System.Drawing.Pen(palette.Divider))
                {
                    args.Graphics.DrawLine(pen, 10, y, Math.Max(11, args.Item.Width - 10), y);
                }
            }
        }

        private void OnTray(Action action)
        {
            Dispatcher.BeginInvoke(action);
        }

        // 显示/隐藏全部分区与便签（DeskBox ToggleTrayWidgetsAsync 语义）：
        // 显示 → 无激活 Show 全部并进入唤起会话（整组脱离桌面层、瞬态置顶脉冲浮到普通
        // 带顶部），用户点击外部窗口/前台变化后由恢复监视器按组送回桌面层；
        // 隐藏 → 先结束会话（组恢复），再逐窗隐藏。
        private void SetPanelsVisible(bool visible)
        {
            if (visible)
            {
                List<IntPtr> handles = new List<IntPtr>();
                foreach (PanelWindow panel in panelWindows.Values)
                {
                    if (panel == null) continue;
                    try { panel.ShowForLayerRaise(); } catch { }
                    if (panel.NativeHandle != IntPtr.Zero) handles.Add(panel.NativeHandle);
                }
                foreach (NoteWindow note in noteWindows.Values)
                {
                    if (note == null) continue;
                    try { note.ShowForLayerRaise(); } catch { }
                    if (note.NativeHandle != IntPtr.Zero && !note.IsPinned) handles.Add(note.NativeHandle);
                }
                LayerSession.RaiseAll(handles);
            }
            else
            {
                List<IntPtr> handles = new List<IntPtr>();
                foreach (PanelWindow panel in panelWindows.Values)
                {
                    if (panel != null && panel.NativeHandle != IntPtr.Zero) handles.Add(panel.NativeHandle);
                }
                foreach (NoteWindow note in noteWindows.Values)
                {
                    if (note != null && note.NativeHandle != IntPtr.Zero && !note.IsPinned) handles.Add(note.NativeHandle);
                }
                LayerSession.HideAll(handles);
                foreach (PanelWindow panel in panelWindows.Values)
                {
                    try { panel.Hide(); } catch { }
                }
            }
        }

        internal List<Rect> GetPanelBounds(string excludedId)
        {
            List<Rect> result = new List<Rect>();
            foreach (KeyValuePair<string, PanelWindow> entry in new List<KeyValuePair<string, PanelWindow>>(panelWindows))
            {
                if (String.Equals(entry.Key, excludedId, StringComparison.OrdinalIgnoreCase)) continue;
                PanelWindow panel = entry.Value;
                if (panel == null || !panel.IsVisible || panel.WindowState == WindowState.Minimized) continue;
                double width = panel.ActualWidth > 0 ? panel.ActualWidth : panel.Width;
                double height = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
                if (Double.IsNaN(panel.Left) || Double.IsNaN(panel.Top) || width <= 0 || height <= 0) continue;
                result.Add(new Rect(panel.Left, panel.Top, width, height));
            }
            return result;
        }

        // 单实例重复启动唤醒：还原/唤醒桌面面板，并将设置窗口强制弹到前台
        internal void OnSingleInstanceActivated()
        {
            try { SetPanelsVisible(true); } catch { }
            ShowSettingsWindow();
            try
            {
                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(2000, "片刻收纳", "片刻收纳正在运行中，已为你呼出界面。", Forms.ToolTipIcon.Info);
                }
            }
            catch { }
        }

        // 首次完成宿主初始化后的反馈：浮起一次桌面部件并冒出托盘气泡
        internal void OnStartupComplete()
        {
            try
            {
                SetPanelsVisible(true);
                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(3000, "片刻收纳", "桌面收纳格子与便签已启动，可在右下角托盘进行管理或双击托盘打开设置。", Forms.ToolTipIcon.Info);
                }
            }
            catch { }
        }

        // 设置窗口：WebView2 环境预热与实例复用，毫秒级秒开呈现
        internal async void ShowSettingsWindow()
        {
            if (!await EnsureWebEnvironment())
            {
                MessageBox.Show("未能初始化 WebView2 运行环境，请确保已安装 Microsoft Edge WebView2 运行时。", "片刻收纳", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                Log("ShowSettingsWindow 正在打开设置窗口...");
                if (settingsWindow == null)
                {
                    settingsWindow = new SettingsWindow(this, webEnvironment, webRoot);
                }

                settingsWindow.Opacity = 1;

                // 强制定位到主屏幕工作区正中央，防止因坐标异常或多显示器切换导致窗口遗失在屏幕外
                Rect workArea = SystemParameters.WorkArea;
                double winW = settingsWindow.Width > 0 ? settingsWindow.Width : 860;
                double winH = settingsWindow.Height > 0 ? settingsWindow.Height : 700;
                settingsWindow.Left = workArea.Left + Math.Max(0, (workArea.Width - winW) / 2);
                settingsWindow.Top = workArea.Top + Math.Max(0, (workArea.Height - winH) / 2);

                if (!settingsWindow.IsVisible)
                {
                    settingsWindow.Show();
                }
                if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
                settingsWindow.Topmost = true;
                settingsWindow.Topmost = false;
                settingsWindow.Activate();
                IntPtr swHwnd = new WindowInteropHelper(settingsWindow).Handle;
                if (swHwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(swHwnd);
                    BringWindowToTop(swHwnd);
                }
                settingsWindow.NotifyRefresh();
            }
            catch (Exception error)
            {
                Log("显示设置窗口异常：" + error.Message);
                settingsWindow = null;
            }
        }

        // WebView2 环境懒创建（设置窗口与迁移 webview 共用；并发调用去重）
        private async Task<bool> EnsureWebEnvironment()
        {
            if (webEnvironment != null && !String.IsNullOrWhiteSpace(webRoot)) return true;
            if (webEnvironmentTask == null) webEnvironmentTask = CreateWebEnvironmentAsync();
            return await webEnvironmentTask;
        }

        private async Task<bool> CreateWebEnvironmentAsync()
        {
            try
            {
                string dataPath = PrepareWebViewDataPath();
                // --renderer-process-limit=1：设置窗口与迁移 webview 共用同一个渲染进程，
                // 省掉一个约 70MB 的渲染进程；本应用零网络需求，其余开关关闭后台联网/组件
                // 更新/同步/翻译/媒体路由等空闲服务，减少后台线程与内存占用。
                CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--no-proxy-server --renderer-process-limit=1 --disable-background-networking --disable-component-update --disable-sync --disable-features=msWebOOUI,msPdfOOUI,Translate,AutofillServerCommunication,OptimizationHints,MediaRouter,CalculateNativeWinOcclusion --js-flags=--max-old-space-size=96 --no-first-run",
                    AllowSingleSignOnUsingOSPrimaryAccount = false,
                    Language = "zh-CN"
                };
                // CreateAsync 参数顺序为 (浏览器可执行目录, 用户数据目录, 选项)：
                // 第一参传 null 表示使用系统安装的 WebView2 运行时。
                webEnvironment = await CoreWebView2Environment.CreateAsync(null, dataPath, options);
                webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
                return true;
            }
            catch (Exception error)
            {
                Log("WebView2 环境创建失败：" + error.Message);
                webEnvironment = null;
                webRoot = null;
                return false;
            }
        }

        // 一次性启动迁移：config.json 缺失时（旧版配置存放在 WebView2 localStorage 中）创建
        // 独立的隐藏迁移 webview，加载 view=migrate 页面自动导入；收到 importConfig/migrationDone
        // 或 30 秒超时/初始化失败后销毁并启动管理器。失败不阻塞启动。
        private async void EnsureConfigMigrated()
        {
            if (File.Exists(GetConfigPath())) return;   // 配置已存在：无需迁移
            // 超时兜底放在最前：任何一步（环境创建/控件初始化/页面加载）卡住都会在 30 秒后
            // 销毁迁移 webview 并启动管理器，保证不阻塞启动。
            migrationTimeoutTimer = new DispatcherTimer(DispatcherPriority.Background);
            migrationTimeoutTimer.Interval = TimeSpan.FromSeconds(30);
            migrationTimeoutTimer.Tick += delegate { DestroyMigrationWebView(); };
            migrationTimeoutTimer.Start();
            try
            {
                if (!await EnsureWebEnvironment())
                {
                    StartManager();
                    return;
                }
                migrationWebView = new WebView2CompositionControl();
                migrationWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                // 迁移 webview 必须挂进视觉树：无视觉树时 EnsureCoreWebView2Async 会永久挂起，
                // 配置迁移将永远无法完成。用一个屏幕外的隐藏窗口承载（1x1、透明、不显示在任务栏）。
                migrationWindow = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Width = 1,
                    Height = 1,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0
                };
                migrationWindow.Content = migrationWebView;
                migrationWindow.Show();
                await migrationWebView.EnsureCoreWebView2Async(webEnvironment);
                CoreWebView2 core = migrationWebView.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.WebMessageReceived += OnMigrationMessage;
                core.NavigationStarting += delegate(object navSender, CoreWebView2NavigationStartingEventArgs navArgs)
                {
                    if (!IsTrustedWebSource(navArgs.Uri)) navArgs.Cancel = true;
                };
                core.NewWindowRequested += delegate(object windowSender, CoreWebView2NewWindowRequestedEventArgs windowArgs)
                {
                    windowArgs.Handled = true;
                };
                core.SetVirtualHostNameToFolderMapping("appassets.example", webRoot, CoreWebView2HostResourceAccessKind.Deny);
                migrationWebView.Source = new Uri("https://appassets.example/index.html?view=migrate");
            }
            catch (Exception error)
            {
                Log("迁移 webview 启动失败：" + error.Message);
                DestroyMigrationWebView();
            }
        }

        // 迁移 webview 消息入口：与设置窗口相同的方式路由到 Execute（importConfig 立即落盘，
        // migrationDone 关闭并销毁本 webview）。迁移结束/失败后确保管理器已启动（默认配置）。
        private async void OnMigrationMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string id = "";
            try
            {
                if (!IsTrustedWebSource(args.Source)) throw new UnauthorizedAccessException("已拒绝非受信页面的迁移请求");
                Dictionary<string, object> request = serializer.DeserializeObject(args.TryGetWebMessageAsString()) as Dictionary<string, object>;
                if (request == null) throw new InvalidOperationException("无效的迁移请求");
                id = Convert.ToString(request["id"]);
                string method = Convert.ToString(request["method"]);
                Dictionary<string, object> values = request.ContainsKey("args") ? request["args"] as Dictionary<string, object> : new Dictionary<string, object>();
                Dictionary<string, object> safeValues = values ?? new Dictionary<string, object>();
                object result = IsBackgroundMethod(method)
                    ? await Task.Run<object>(() => Execute(method, safeValues))
                    : Execute(method, safeValues);
                PostMigrationResponse(id, result, null);
            }
            catch (Exception error)
            {
                Log("处理迁移请求失败：" + error.Message);
                PostMigrationResponse(id, null, error.Message);
            }
        }

        private void PostMigrationResponse(string id, object result, string error)
        {
            try
            {
                if (migrationWebView == null || migrationWebView.CoreWebView2 == null) return;
                Dictionary<string, object> response = new Dictionary<string, object>();
                response["id"] = id;
                if (error == null) response["result"] = result;
                else response["error"] = error;
                migrationWebView.CoreWebView2.PostWebMessageAsJson(serializer.Serialize(response));
            }
            catch { }
        }

        // 销毁迁移 webview（UI 线程调用）；startManager=true 时若管理器尚未启动则用默认配置启动
        private void DestroyMigrationWebView(bool startManager = true)
        {
            try
            {
                if (migrationTimeoutTimer != null)
                {
                    migrationTimeoutTimer.Stop();
                    migrationTimeoutTimer = null;
                }
            }
            catch { }
            WebView2CompositionControl closing = migrationWebView;
            migrationWebView = null;
            if (closing != null)
            {
                try { closing.CoreWebView2.WebMessageReceived -= OnMigrationMessage; } catch { }
                try { closing.Dispose(); } catch { }
            }
            System.Windows.Window closingWindow = migrationWindow;
            migrationWindow = null;
            if (closingWindow != null)
            {
                try { closingWindow.Close(); } catch { }
            }
            if (startManager) StartManager();
        }

        // 退出：先 Close 触发 OnClosed 清理（含 manager.Dispose 落盘），再结束消息循环
        private void ExitApplication()
        {
            try { Close(); } catch { }
            try { Application.Current.Shutdown(); } catch { }
        }

        // 新建分区：WPF 对话框（CategoryDialog）→ 解析 Manager 当前配置 → 注入新分类 → ApplyConfig
        private void OpenCategoryDialog()
        {
            try
            {
                List<string> existingNames = new List<string>();
                Dictionary<string, object> config = null;
                try
                {
                    object parsed = serializer.DeserializeObject(manager.SerializeConfig());
                    config = parsed as Dictionary<string, object>;
                }
                catch { }
                if (config == null) config = new Dictionary<string, object>();
                Dictionary<string, object> prefs = config.ContainsKey("preferences") ? config["preferences"] as Dictionary<string, object> : null;
                if (prefs == null)
                {
                    prefs = new Dictionary<string, object>();
                    config["preferences"] = prefs;
                }
                object[] rawCategories = prefs.ContainsKey("categories") ? prefs["categories"] as object[] : null;
                if (rawCategories != null)
                {
                    foreach (object raw in rawCategories)
                    {
                        Dictionary<string, object> entry = raw as Dictionary<string, object>;
                        if (entry == null) continue;
                        object name;
                        if (entry.TryGetValue("name", out name) && name != null) existingNames.Add(Convert.ToString(name));
                    }
                }
                CategoryDialog dialog = new CategoryDialog(existingNames);
                if (dialog.ShowDialog() != true || dialog.Result == null) return;
                CategoryData category = dialog.Result;
                // 注入新分类（字段与前端 CategoryModal 生成的条目一致，Manager 会再归一化）
                List<object> next = new List<object>();
                if (rawCategories != null) foreach (object raw in rawCategories) next.Add(raw);
                Dictionary<string, object> categoryEntry = new Dictionary<string, object>();
                categoryEntry["id"] = category.Id;
                categoryEntry["name"] = category.Name;
                categoryEntry["icon"] = category.Icon;
                categoryEntry["color"] = category.Color;
                categoryEntry["extensions"] = category.Extensions;
                categoryEntry["acceptsFolders"] = category.AcceptsFolders;
                next.Add(categoryEntry);
                prefs["categories"] = next.ToArray();
                manager.ApplyConfig(config);
            }
            catch (Exception error)
            {
                Log("新建分区失败：" + error.Message);
            }
        }

        private void OnSettingsWindowClosed(object sender, EventArgs args)
        {
            if (settingsWindow == sender) settingsWindow = null;
            // 窗口走自身 X 关闭时同样裁剪工作集，回收其 DOM/JS 堆占用
            TrimWorkingSet();
        }

        internal void CloseSettingsWindow()
        {
            if (settingsWindow != null)
            {
                settingsWindow.FlushAndHide();
            }
        }

        internal void OnSettingsWindowHidden()
        {
            TrimWorkingSet();
        }

        private IntPtr WndProc(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 宿主窗口不显示，只保留最小拦截（收纳条不能被“最小化所有窗口”收走、全局热键）。
            if (message == WmSysCommand && (wParam.ToInt32() & 0xFFF0) == ScMinimize)
            {
                handled = true;
                return IntPtr.Zero;
            }
            if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
            {
                manager.ToggleClickThrough();
                handled = true;
            }
            if (message == WmHotKey && wParam.ToInt32() == SettingsHotKeyId)
            {
                ShowSettingsWindow();
                handled = true;
            }
            if (message == WmShowSettings)
            {
                ShowSettingsWindow();
                handled = true;
            }
            if (message == WmHotKey && wParam.ToInt32() == SearchHotKeyId)
            {
                ShowQuickSearch();
                handled = true;
            }
            // Explorer 重启广播：SHELLDLL_DefView/owner 句柄已失效——失效缓存重建后把宿主
            // 与全部分区重新挂回桌面层，并重挂托盘图标（NotifyIcon 不会自行恢复）。
            if (taskbarCreatedMessage != 0 && message == taskbarCreatedMessage)
            {
                WidgetLayer.InvalidateDesktopIconViewCache();
                WidgetLayer.Attach(hwnd);
                foreach (PanelWindow panel in new List<PanelWindow>(panelWindows.Values))
                {
                    try { WidgetLayer.Attach(panel.NativeHandle); } catch { }
                }
                try { trayIcon.Visible = false; trayIcon.Visible = true; } catch { }
                Log("检测到 Explorer 重启，已重建桌面层挂载与托盘图标");
            }
            return IntPtr.Zero;
        }

        private void OnStateChanged(object sender, EventArgs args)
        {
            // 兜底：即使有其它路径直接调用了 ShowWindow(SW_MINIMIZE)，
            // 也立即把桌面宿主恢复为正常状态。设置窗口不在此列，仍可被最小化。
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        }

        // 切换鼠标穿透：由 Manager.ToggleClickThrough 驱动（接口回调），面板联动
        private void SetClickThrough(bool enabled)
        {
            clickThrough = enabled;
            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            if (enabled) style |= WsExTransparent;
            else style &= ~WsExTransparent;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
            foreach (PanelWindow panel in panelWindows.Values) panel.SetClickThrough(enabled);
        }

        internal void ShowQuickSearch()
        {
            if (closed) return;
            try
            {
                if (quickSearchWindow == null)
                {
                    quickSearchWindow = new QuickSearchWindow(this);
                    quickSearchWindow.Closed += delegate { quickSearchWindow = null; };
                }
                if (!quickSearchWindow.IsVisible) quickSearchWindow.Show();
                else
                {
                    if (quickSearchWindow.WindowState == WindowState.Minimized) quickSearchWindow.WindowState = WindowState.Normal;
                    quickSearchWindow.Activate();
                }
            }
            catch (InvalidOperationException)
            {
                quickSearchWindow = null;
            }
        }

        internal List<QuickSearchEntry> GetQuickSearchEntries()
        {
            return manager.GetQuickSearchEntries();
        }

        internal void OpenQuickSearchPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            try
            {
                manager.HandlePanelEvent("itemOpened", path);
                OpenItem(path);
            }
            catch (Exception error) { Log("快速搜索打开失败：" + error.Message); }
        }

        internal static bool IsBackgroundMethod(string method)
        {
            return method == "readDirectory" || method == "scanDirectory" || method == "getStats" || method == "getIcon" ||
                   method == "createDirectory" || method == "move" || method == "open" || method == "checkUpdate";
        }

        internal static bool IsTrustedWebSource(string source)
        {
            Uri uri;
            return Uri.TryCreate(source, UriKind.Absolute, out uri) &&
                String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                (String.Equals(uri.Host, "appassets.example", StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(uri.Host, "pivkey.local", StringComparison.OrdinalIgnoreCase));
        }

        internal object Execute(string method, Dictionary<string, object> values)
        {
            if (method == "getAppVersion")
            {
                return UpdateManager.GetVersionInfo();
            }
            if (method == "checkUpdate")
            {
                string repo = values != null && values.ContainsKey("repo") && values["repo"] != null ? Convert.ToString(values["repo"]) : null;
                return UpdateManager.CheckForUpdates(repo);
            }
            if (method == "startDownloadUpdate")
            {
                string downloadUrl = Required(values, "downloadUrl");
                return UpdateManager.StartDownload(downloadUrl);
            }
            if (method == "getDownloadProgress")
            {
                return UpdateManager.GetDownloadProgress();
            }
            if (method == "applyUpdate")
            {
                string filePath = values != null && values.ContainsKey("filePath") && values["filePath"] != null ? Convert.ToString(values["filePath"]) : null;
                bool silent = values != null && values.ContainsKey("silent") && Convert.ToBoolean(values["silent"]);
                bool launched = UpdateManager.LaunchInstaller(filePath, silent);
                if (launched)
                {
                    Task.Run(delegate
                    {
                        System.Threading.Thread.Sleep(600);
                        Dispatcher.BeginInvoke(new Action(delegate { ExitApplication(); }));
                    });
                }
                return launched;
            }
            if (method == "managerCommand")
            {
                manager.HandleTrayCommand(Required(values, "command"));
                return true;
            }
            if (method == "createNote")
            {
                string mode = values != null && values.ContainsKey("mode") && values["mode"] != null ? Convert.ToString(values["mode"]) : "todo";
                string title = values != null && values.ContainsKey("title") && values["title"] != null ? Convert.ToString(values["title"]) : null;
                manager.CreateNoteWithMode(mode, title);
                return true;
            }
            if (method == "deleteNote")
            {
                string id = values != null && values.ContainsKey("id") && values["id"] != null ? Convert.ToString(values["id"]) : null;
                if (!String.IsNullOrWhiteSpace(id)) manager.DeleteNote(id);
                return true;
            }
            if (method == "organizationPreview") return manager.GetOrganizationPreview();
            if (method == "historySummary") return manager.GetHistorySummary();
            if (method == "exportConfig") return ExportConfigFile();
            if (method == "importConfigFile") return ImportConfigFile();
            if (method == "loadConfig")
            {
                // 设置窗口初始化加载：Manager 序列化当前配置后解析为字典（无配置时为默认配置）
                try
                {
                    object payload = serializer.DeserializeObject(manager.SerializeConfig());
                    // 主配置曾损坏并从快照恢复时，把提示带给设置页显式报告给用户
                    string recovery = manager.ConsumeConfigRecoveryNotice();
                    Dictionary<string, object> record = payload as Dictionary<string, object>;
                    if (record != null && !String.IsNullOrEmpty(recovery)) record["configRecovery"] = recovery;
                    return payload;
                }
                catch
                {
                    return null;
                }
            }
            if (method == "saveConfig" || method == "importConfig")
            {
                // 设置页/迁移页保存配置：交 Manager 归一化、应用（防抖落盘）并重扫
                object rawConfig;
                Dictionary<string, object> config = values.TryGetValue("config", out rawConfig) ? rawConfig as Dictionary<string, object> : null;
                manager.ApplyConfig(config);
                if (method == "importConfig") WriteConfigFromManager();   // 迁移数据立即落盘
                return true;
            }
            if (method == "migrationDone")
            {
                // 迁移页完成：关闭并销毁迁移 webview（管理器尚未启动时用当前配置启动）
                DestroyMigrationWebView();
                return true;
            }
            if (method == "getDesktopPath")
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                EnsureDesktopWatchers(desktopPath);
                return desktopPath;
            }
            if (method == "pickFolder") return PickFolderCore();
            if (method == "readDirectory") return ReadDirectory(EnsureDesktopPath(Required(values, "path")));
            if (method == "scanDirectory") return ScanDirectory(EnsureDesktopPath(Required(values, "path")), values.ContainsKey("refreshIcons") && Convert.ToBoolean(values["refreshIcons"]));
            if (method == "getStats") return GetStats(EnsureDesktopPath(Required(values, "path")));
            if (method == "getIcon") return GetCachedShellIcon(EnsureDesktopPath(Required(values, "path")), values.ContainsKey("refresh") && Convert.ToBoolean(values["refresh"]));
            if (method == "createDirectory") { Directory.CreateDirectory(EnsureManagedPath(Required(values, "path"))); return true; }
            if (method == "move")
            {
                string source = EnsureDesktopPath(Required(values, "source"));
                string destination = EnsureMoveDestination(Required(values, "destination"));
                MoveItem(source, destination);
                InvalidatePathCache(source);
                InvalidatePathCache(destination);
                return true;
            }
            if (method == "moveIntoCategory")
            {
                string categoryId = Required(values, "categoryId");
                string[] movePaths = ReadStringArray(values, "paths");
                MoveBatchResult result = MoveIntoCategory(categoryId, movePaths);
                manager.HandleDesktopChanged();   // 移动后防抖重扫
                return result;
            }
            if (method == "watchPortalFolders")
            {
                EnsurePortalWatchers(ReadStringArray(values, "paths"));
                return true;
            }
            if (method == "open") { OpenItem(EnsureDesktopPath(Required(values, "path"))); return true; }
            if (method == "closeSettings") { CloseSettingsWindow(); return true; }
            throw new InvalidOperationException("未知的桌面命令：" + method);
        }

        // Manager 直接下发强类型面板负载（旧的对象[]→强类型解析已随管理器 webview 移除）
        void IManagerHost.SyncPanels(PanelSyncData[] typed)
        {
            if (typed == null) return;
            foreach (PanelSyncData value in typed)
            {
                if (value == null || String.IsNullOrWhiteSpace(value.theme)) continue;
                ApplyTrayMenuPalette(value);
                ApplyUiScale(value.uiScale);
                break;
            }
            if (!startupSyncDone)
            {
                // 首次同步完成：等面板与便签构建结束（调度到 ApplicationIdle 优先级），向用户播放浮起脉冲并冒出托盘气泡
                startupSyncDone = true;
                ScheduleStartupHeapCompact();
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
                {
                    OnStartupComplete();
                }));
            }
            HashSet<string> live = new HashSet<string>();
            foreach (PanelSyncData value in typed)
            {
                if (value == null || String.IsNullOrWhiteSpace(value.id)) continue;
                string id = value.id;
                live.Add(id);
                try
                {
                    // 每个面板独立创建：单面板异常只记日志，不影响其余面板。
                    PanelWindow panel;
                    if (!panelWindows.TryGetValue(id, out panel))
                    {
                        panel = new PanelWindow(this, id);
                        panelWindows[id] = panel;
                        panel.Closed += delegate { panelWindows.Remove(id); panelNames.Remove(id); panelReadOnly.Remove(id); };
                        // 先同步内容与几何、再显示：窗口一出现就在最终位置/尺寸，避免"默认位置闪一下再跳变"
                        panel.UpdateContent(value);
                        panel.UpdateBounds(value.x, value.y, value.width, value.height, value.pinned);
                        if (!String.IsNullOrWhiteSpace(value.name)) panelNames[id] = value.name;
                        panelReadOnly[id] = value.readOnly;
                        panel.Show();
                    }
                    else
                    {
                        if (!String.IsNullOrWhiteSpace(value.name)) panelNames[id] = value.name;
                        panelReadOnly[id] = value.readOnly;
                        panel.UpdateContent(value);
                        panel.UpdateBounds(value.x, value.y, value.width, value.height, value.pinned);
                    }
                    panel.SetClickThrough(clickThrough);
                }
                catch (Exception error)
                {
                    Log("面板创建/同步失败：" + id + " " + error.Message);
                }
            }
            foreach (string id in new List<string>(panelWindows.Keys))
            {
                if (live.Contains(id)) continue;
                panelWindows[id].Close();
                panelWindows.Remove(id);
                panelNames.Remove(id);
                panelReadOnly.Remove(id);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CursorPoint { public int X; public int Y; }
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out CursorPoint point);

        internal void OnCreateNoteFromContextMenu(string mode)
        {
            CursorPoint pt;
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["mode"] = mode;
            if (GetCursorPos(out pt))
            {
                double scale = 1.0;
                try
                {
                    PresentationSource source = PresentationSource.FromVisual(this);
                    if (source != null && source.CompositionTarget != null)
                    {
                        scale = source.CompositionTarget.TransformToDevice.M11;
                    }
                }
                catch { }
                payload["x"] = pt.X / scale;
                payload["y"] = pt.Y / scale;
            }
            PostManagerEvent("noteCreate", payload);
        }

        internal static void SyncDesktopContextMenu(bool enable)
        {
            string keyPath = @"Software\Classes\Directory\Background\shell\PivkeyNotes";
            if (!enable)
            {
                try
                {
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
                }
                catch { }
                return;
            }
            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    if (key != null)
                    {
                        key.SetValue("MUIVerb", "新建片刻便签");
                        key.SetValue("Icon", "\"" + exePath + "\",0");
                        key.SetValue("SubCommands", "");

                        using (Microsoft.Win32.RegistryKey shell = key.CreateSubKey("shell"))
                        {
                            if (shell != null)
                            {
                                using (Microsoft.Win32.RegistryKey todo = shell.CreateSubKey("1_todo"))
                                {
                                    if (todo != null)
                                    {
                                        todo.SetValue("", "新建待办清单");
                                        todo.SetValue("Icon", "\"" + exePath + "\",0");
                                        using (Microsoft.Win32.RegistryKey cmd = todo.CreateSubKey("command"))
                                        {
                                            if (cmd != null) cmd.SetValue("", "\"" + exePath + "\" --create-todo");
                                        }
                                    }
                                }
                                using (Microsoft.Win32.RegistryKey note = shell.CreateSubKey("2_note"))
                                {
                                    if (note != null)
                                    {
                                        note.SetValue("", "新建随手备忘");
                                        note.SetValue("Icon", "\"" + exePath + "\",0");
                                        using (Microsoft.Win32.RegistryKey cmd = note.CreateSubKey("command"))
                                        {
                                            if (cmd != null) cmd.SetValue("", "\"" + exePath + "\" --create-note");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("同步桌面右键菜单失败: " + ex.Message);
            }
        }

        void IManagerHost.SyncNotes(List<NoteData> notes, bool noteCapsuleMode, bool desktopContextMenu)
        {
            SyncDesktopContextMenu(desktopContextMenu);
            SyncNotes(notes, noteCapsuleMode);
        }

        private void SyncNotes(List<NoteData> notes, bool noteCapsuleMode)
        {
            if (notes == null) return;
            HashSet<string> live = new HashSet<string>();
            foreach (NoteData note in notes)
            {
                if (note == null || String.IsNullOrWhiteSpace(note.Id)) continue;
                string id = note.Id;
                live.Add(id);
                try
                {
                    NoteWindow win;
                    if (!noteWindows.TryGetValue(id, out win) || win == null)
                    {
                        win = new NoteWindow(this, note);
                        noteWindows[id] = win;
                        win.ApplyCapsuleMode(noteCapsuleMode);
                        win.ApplyThemeVisuals(currentTheme, currentAccent, currentHeaderSurface, currentGlassOpacity, currentMaterial);
                        win.Show();
                    }
                    else
                    {
                        win.ApplyCapsuleMode(noteCapsuleMode);
                        win.UpdateData(note);
                        win.ApplyThemeVisuals(currentTheme, currentAccent, currentHeaderSurface, currentGlassOpacity, currentMaterial);
                    }
                }
                catch (Exception error)
                {
                    Log("便签创建/同步失败：" + id + " " + error.Message);
                }
            }
            foreach (string id in new List<string>(noteWindows.Keys))
            {
                if (live.Contains(id)) continue;
                try { noteWindows[id].Close(); } catch { }
                noteWindows.Remove(id);
            }
        }

        // 首次面板同步完成后延迟约 4 秒，在后台线程压缩一次托管堆：
        // 回收启动扫描与图标解码期间产生的临时大数组（base64 解码等），降低工作集峰值。
        // GC 在任意线程调用均安全（线程池线程执行，不阻塞 UI 线程）；仅执行一次。
        private void ScheduleStartupHeapCompact()
        {
            try
            {
                Task.Delay(6000).ContinueWith(delegate
                {
                    try
                    {
                        GC.Collect(2, GCCollectionMode.Optimized);
                    }
                    catch { }
                });
            }
            catch { }
        }

        // 后台线程温和压缩托管堆（绝不占用 UI 线程，避免强杀工作集破坏 WebView2 稳定性）：
        // 仅调用 GC.Collect(2, GCCollectionMode.Optimized)，异常一律静默。
        private static void TrimWorkingSet()
        {
            try
            {
                Task.Run(delegate
                {
                    try
                    {
                        GC.Collect(2, GCCollectionMode.Optimized);
                    }
                    catch { }
                });
            }
            catch { }
        }

        internal static void ApplyWindowRegion(IntPtr target, List<RegionRect> regions)
        {
            if (target == IntPtr.Zero) return;
            IntPtr combined = CreateRectRgn(0, 0, 0, 0);
            try
            {
                foreach (RegionRect rect in regions)
                {
                    IntPtr part = rect.Radius > 0 ? CreateRoundRectRgn(rect.Left, rect.Top, rect.Right + 1, rect.Bottom + 1, rect.Radius * 2, rect.Radius * 2) : CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom);
                    CombineRgn(combined, combined, part, RgnOr);
                    DeleteObject(part);
                }
                if (SetWindowRgn(target, combined, true) != 0) combined = IntPtr.Zero;
            }
            finally
            {
                if (combined != IntPtr.Zero) DeleteObject(combined);
            }
        }

        private static string Required(Dictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value) || value == null) throw new ArgumentException("缺少参数：" + key);
            return Convert.ToString(value);
        }

        internal static string GetManagedCategoryPath(string categoryName)
        {
            string safeName = ValidateCategoryName(categoryName);
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "片刻收纳");
            return EnsureInside(root, Path.Combine(root, safeName), false);
        }

        private static string ValidateCategoryName(string categoryName)
        {
            if (String.IsNullOrWhiteSpace(categoryName)) throw new ArgumentException("分类名称不能为空");
            string name = categoryName.Trim();
            if (name == "." || name == "..") throw new ArgumentException("分类名称不能是“.”或“..”");
            if (name.Length > 20) throw new ArgumentException("分类名称不能超过 20 个字符");
            if (categoryName.EndsWith(" ", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal))
                throw new ArgumentException("分类名称不能以空格或句点结尾");
            foreach (char character in Path.GetInvalidFileNameChars())
            {
                if (name.IndexOf(character) >= 0) throw new ArgumentException("分类名称包含 Windows 不允许的字符");
            }
            string baseName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (baseName == "CON" || baseName == "PRN" || baseName == "AUX" || baseName == "NUL" ||
                (baseName.StartsWith("COM", StringComparison.Ordinal) && IsReservedDeviceNumber(baseName.Substring(3))) ||
                (baseName.StartsWith("LPT", StringComparison.Ordinal) && IsReservedDeviceNumber(baseName.Substring(3))))
                throw new ArgumentException("分类名称使用了 Windows 保留名称");
            return name;
        }

        private static bool IsReservedDeviceNumber(string value)
        {
            int number;
            return Int32.TryParse(value, out number) && number >= 1 && number <= 9;
        }

        private static string EnsureDesktopPath(string path)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return EnsureInside(desktop, path, true);
        }

        private static string EnsureManagedPath(string path)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string currentRoot = Path.Combine(desktop, "片刻收纳");
            string legacyRoot = Path.Combine(desktop, "轻屿收纳");
            string full = Path.GetFullPath(path);
            if (IsInside(currentRoot, full, true) || IsInside(legacyRoot, full, true)) return full;
            throw new UnauthorizedAccessException("目录必须位于片刻收纳的受管目录内");
        }

        private static string EnsureMoveDestination(string path)
        {
            string desktop = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            string full = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(full);
            if (String.Equals(parent, desktop, StringComparison.OrdinalIgnoreCase)) return full;
            string currentRoot = Path.Combine(desktop, "片刻收纳");
            string legacyRoot = Path.Combine(desktop, "轻屿收纳");
            if (IsInside(currentRoot, full, false) || IsInside(legacyRoot, full, false)) return full;
            throw new UnauthorizedAccessException("文件目标超出桌面或片刻收纳目录");
        }

        private static string EnsureInside(string root, string path, bool allowRoot)
        {
            string full = Path.GetFullPath(path);
            if (!IsInside(root, full, allowRoot)) throw new UnauthorizedAccessException("路径超出允许的桌面范围");
            return full;
        }

        private static bool IsInside(string root, string path, bool allowRoot)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path);
            if (allowRoot && String.Equals(fullRoot, fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return true;
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> ReadDirectory(string path)
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            List<Dictionary<string, object>> entries = new List<Dictionary<string, object>>();
            foreach (string directory in Directory.GetDirectories(path)) entries.Add(Entry(Path.GetFileName(directory), "DIRECTORY"));
            foreach (string file in Directory.GetFiles(path)) entries.Add(Entry(Path.GetFileName(file), "FILE"));
            return entries;
        }

        private static Dictionary<string, object> Entry(string name, string type)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>();
            entry["entry"] = name;
            entry["type"] = type;
            return entry;
        }

        private List<Dictionary<string, object>> ScanDirectory(string path, bool refreshIcons)
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            EnsureDesktopWatchers(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            List<Dictionary<string, object>> entries = new List<Dictionary<string, object>>();
            HashSet<string> live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in Directory.GetDirectories(path))
            {
                live.Add(directory);
                entries.Add(ScannedEntry(directory, "DIRECTORY", refreshIcons));
            }
            foreach (string file in Directory.GetFiles(path))
            {
                live.Add(file);
                entries.Add(ScannedEntry(file, "FILE", refreshIcons));
            }
            PruneScanCacheForDirectory(path, live);
            return entries;
        }

        private Dictionary<string, object> ScannedEntry(string path, string type, bool refreshIcons)
        {
            long writeTicks;
            long size;
            ReadPathStats(path, type, out writeTicks, out size);
            bool hidden = (File.GetAttributes(path) & FileAttributes.Hidden) == FileAttributes.Hidden;

            lock (shellIconCacheLock)
            {
                ScanCacheItem cached;
                if (!refreshIcons &&
                    scanEntryCache.TryGetValue(path, out cached) &&
                    cached.WriteTimeUtcTicks == writeTicks &&
                    cached.Size == size &&
                    cached.Hidden == hidden &&
                    String.Equals(cached.Type, type, StringComparison.Ordinal))
                {
                    return CloneScanEntry(cached);
                }
            }

            string iconUrl = GetCachedShellIcon(path, refreshIcons, writeTicks);
            ScanCacheItem item = new ScanCacheItem();
            item.EntryName = Path.GetFileName(path);
            item.Type = type;
            item.WriteTimeUtcTicks = writeTicks;
            item.Size = size;
            item.Hidden = hidden;
            item.IconUrl = iconUrl;
            lock (shellIconCacheLock) scanEntryCache[path] = item;
            return CloneScanEntry(item);
        }

        private static Dictionary<string, object> CloneScanEntry(ScanCacheItem item)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>();
            entry["entry"] = item.EntryName;
            entry["type"] = item.Type;
            entry["size"] = item.Size;
            entry["hidden"] = item.Hidden;
            entry["modifiedAt"] = new DateTime(item.WriteTimeUtcTicks, DateTimeKind.Utc).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            entry["iconUrl"] = item.IconUrl;
            return entry;
        }

        private static void ReadPathStats(string path, string type, out long writeTicks, out long size)
        {
            if (type == "DIRECTORY" || Directory.Exists(path))
            {
                DirectoryInfo info = new DirectoryInfo(path);
                writeTicks = info.LastWriteTimeUtc.Ticks;
                size = 0L;
                return;
            }
            FileInfo file = new FileInfo(path);
            writeTicks = file.LastWriteTimeUtc.Ticks;
            size = file.Length;
        }

        private void PruneScanCacheForDirectory(string directory, HashSet<string> live)
        {
            string prefix = directory.TrimEnd('\\', '/') + "\\";
            List<string> stale = new List<string>();
            lock (shellIconCacheLock)
            {
                foreach (string key in scanEntryCache.Keys)
                {
                    if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = key.Substring(prefix.Length);
                    if (rest.IndexOf('\\') >= 0 || rest.IndexOf('/') >= 0) continue;
                    if (!live.Contains(key)) stale.Add(key);
                }
                foreach (string key in stale)
                {
                    scanEntryCache.Remove(key);
                    shellIconCache.Remove(key);
                }
            }
        }

        private static Dictionary<string, object> GetStats(string path)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            DateTime modified;
            if (Directory.Exists(path)) { modified = new DirectoryInfo(path).LastWriteTimeUtc; result["size"] = 0L; }
            else if (File.Exists(path)) { FileInfo info = new FileInfo(path); modified = info.LastWriteTimeUtc; result["size"] = info.Length; }
            else throw new FileNotFoundException(path);
            result["modifiedAt"] = (modified - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            return result;
        }

        // 单项移动走原生操作桥：同卷 rename（原子、快），跨卷自动退回 Shell 静默移动
        // （.NET Framework 的 File.Move 不支持跨卷）
        private static void MoveItem(string source, string destination)
        {
            NativeFileOps.MoveSingle(source, destination);
        }

        private static void OpenItem(string path)
        {
            ShellLauncher.Open(path);
        }

        private string GetCachedShellIcon(string path)
        {
            return GetCachedShellIcon(path, false, -1);
        }

        private string GetCachedShellIcon(string path, bool forceRefresh)
        {
            return GetCachedShellIcon(path, forceRefresh, -1);
        }

        private string GetCachedShellIcon(string path, bool forceRefresh, long writeTicks)
        {
            if (writeTicks < 0)
            {
                try
                {
                    if (Directory.Exists(path)) writeTicks = new DirectoryInfo(path).LastWriteTimeUtc.Ticks;
                    else if (File.Exists(path)) writeTicks = new FileInfo(path).LastWriteTimeUtc.Ticks;
                    else writeTicks = 0;
                }
                catch { writeTicks = 0; }
            }

            lock (shellIconCacheLock)
            {
                IconCacheItem cached;
                if (!forceRefresh && shellIconCache.TryGetValue(path, out cached) && cached.WriteTimeUtcTicks == writeTicks)
                {
                    cached.LastAccessTicks = DateTime.UtcNow.Ticks;
                    return cached.Data;
                }
            }

            // 磁盘缓存：非刷新且内存未命中时先查磁盘，命中则直接使用；
            // 未命中则提取图标后写盘，供下次启动复用，缩短冷启动扫描耗时。
            string icon = null;
            if (!forceRefresh)
            {
                try
                {
                    string cacheFile = GetIconCacheFilePath(path, iconCacheEpoch);
                    if (File.Exists(cacheFile))
                    {
                        byte[] bytes = File.ReadAllBytes(cacheFile);
                        if (bytes.Length > 0) icon = "data:image/png;base64," + Convert.ToBase64String(bytes);
                    }
                }
                catch { }
            }
            if (icon == null)
            {
                icon = GetShellIcon(path);
                WriteIconCacheFile(path, icon, iconCacheEpoch);
            }

            lock (shellIconCacheLock)
            {
                IconCacheItem item = new IconCacheItem();
                item.Data = icon;
                item.WriteTimeUtcTicks = writeTicks;
                item.LastAccessTicks = DateTime.UtcNow.Ticks;
                shellIconCache[path] = item;
                TrimIconCacheUnlocked();
            }
            return icon;
        }

        private void TrimIconCacheUnlocked()
        {
            while (shellIconCache.Count > MaxIconCacheEntries)
            {
                string victim = null;
                long oldest = long.MaxValue;
                foreach (KeyValuePair<string, IconCacheItem> pair in shellIconCache)
                {
                    if (pair.Value.LastAccessTicks < oldest)
                    {
                        oldest = pair.Value.LastAccessTicks;
                        victim = pair.Key;
                    }
                }
                if (victim == null) break;
                shellIconCache.Remove(victim);
            }
        }

        private void EnsureDesktopWatchers(string desktopPath)
        {
            if (String.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath)) return;
            if (desktopChangeDebounce == null)
            {
                desktopChangeDebounce = new DispatcherTimer(DispatcherPriority.Background);
                desktopChangeDebounce.Interval = TimeSpan.FromMilliseconds(450);
                desktopChangeDebounce.Tick += delegate
                {
                    desktopChangeDebounce.Stop();
                    manager.HandleDesktopChanged();
                };
            }

            if (!String.Equals(watchedDesktopPath, desktopPath, StringComparison.OrdinalIgnoreCase) || fileWatchers.Count == 0)
            {
                DisposeDesktopWatchers();
                watchedDesktopPath = desktopPath;
                AddDirectoryWatcher(desktopPath, false);
            }

            string[] managedRoots = new string[] { "片刻收纳", "轻屿收纳" };
            foreach (string name in managedRoots)
            {
                string managedPath = Path.Combine(desktopPath, name);
                if (!Directory.Exists(managedPath)) continue;
                bool already = false;
                foreach (FileSystemWatcher watcher in fileWatchers)
                {
                    if (String.Equals(watcher.Path, managedPath, StringComparison.OrdinalIgnoreCase)) { already = true; break; }
                }
                if (!already) AddDirectoryWatcher(managedPath, true);
            }
        }

        private void AddDirectoryWatcher(string path, bool includeSubdirectories)
        {
            try
            {
                FileSystemWatcher watcher = new FileSystemWatcher(path);
                watcher.IncludeSubdirectories = includeSubdirectories;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.InternalBufferSize = 64 * 1024;
                FileSystemEventHandler handler = OnDesktopFileSystemEvent;
                RenamedEventHandler renamed = OnDesktopFileSystemRenamed;
                watcher.Changed += handler;
                watcher.Created += handler;
                watcher.Deleted += handler;
                watcher.Renamed += renamed;
                watcher.Error += OnDesktopWatcherError;
                watcher.EnableRaisingEvents = true;
                fileWatchers.Add(watcher);
            }
            catch (Exception error)
            {
                // Watcher is best-effort; polling remains as a backup.
                Log("创建桌面文件监视器失败：" + path + " " + error.Message);
            }
        }

        private void OnDesktopFileSystemEvent(object sender, FileSystemEventArgs args)
        {
            ScheduleDesktopChanged();
        }

        private void OnDesktopFileSystemRenamed(object sender, RenamedEventArgs args)
        {
            ScheduleDesktopChanged();
        }

        private void OnDesktopWatcherError(object sender, ErrorEventArgs args)
        {
            Log("桌面文件监视器发生错误，已安排完整重扫：" + (args.GetException() == null ? "未知错误" : args.GetException().Message));
            // Watcher 自愈（DeskBox FolderWatcherHealth 语义）：缓冲溢出/句柄失效后旧 watcher
            // 不再产出事件，移除后由一次性延迟任务重建；失败项仍由重扫兜底。
            FileSystemWatcher watcher = sender as FileSystemWatcher;
            if (watcher != null)
            {
                string path = watcher.Path;
                bool includeSubdirectories = watcher.IncludeSubdirectories;
                bool isPortal = portalWatchers.Contains(watcher);
                List<FileSystemWatcher> source = isPortal ? portalWatchers : fileWatchers;
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
                try { Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { source.Remove(watcher); })); } catch { }
                ScheduleWatcherRecreate(path, includeSubdirectories, isPortal);
            }
            ScheduleDesktopChanged();
        }

        // 一次性延迟重建（2 秒后，UI 线程执行）：瞬时错误（缓冲溢出/网络抖动）不循环重建
        private void ScheduleWatcherRecreate(string path, bool includeSubdirectories, bool portal)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            try
            {
                System.Threading.Timer timer = null;
                timer = new System.Threading.Timer(delegate
                {
                    try { if (timer != null) timer.Dispose(); } catch { }
                    try
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                        {
                            if (!Directory.Exists(path)) return;
                            if (portal) AddPortalDirectoryWatcher(path);
                            else AddDirectoryWatcher(path, includeSubdirectories);
                        }));
                    }
                    catch { }
                }, null, 2000, System.Threading.Timeout.Infinite);
            }
            catch { }
        }

        private void ScheduleDesktopChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (desktopChangeDebounce == null) return;
                    desktopChangeDebounce.Stop();
                    desktopChangeDebounce.Start();
                }));
            }
            catch (Exception error) { Log("调度桌面刷新失败：" + error.Message); }
        }

        private void DisposeDesktopWatchers()
        {
            foreach (FileSystemWatcher watcher in fileWatchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { }
            }
            fileWatchers.Clear();
            watchedDesktopPath = null;
        }

        // 追加写入 %LocalAppData%\PivkeyOrganizer\pivkey.log；异常静默。
        internal static void Log(string message)
        {
            try
            {
                string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (String.IsNullOrWhiteSpace(localData)) return;
                string directory = Path.Combine(localData, "PivkeyOrganizer");
                Directory.CreateDirectory(directory);
                string line = String.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}", DateTime.Now, message, Environment.NewLine);
                File.AppendAllText(Path.Combine(directory, "pivkey.log"), line, Encoding.UTF8);
            }
            catch { }
        }

        // ---- 图标磁盘缓存：键为路径小写的 SHA1 前 16 位 hex，值为 {hash}.png ----
        private static bool iconCachePurged;

        private static string GetIconCacheDirectory()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PivkeyOrganizer", "IconCache");
            if (!iconCachePurged)
            {
                iconCachePurged = true;
                try
                {
                    // 分辨率升级后清理旧版（64px）图标缓存；marker 保证只执行一次
                    string marker = Path.Combine(dir, "hires-v2.marker");
                    Directory.CreateDirectory(dir);
                    if (!File.Exists(marker))
                    {
                        foreach (string stale in Directory.EnumerateFiles(dir, "*.png")) File.Delete(stale);
                        File.WriteAllText(marker, "hires icon cache v2");
                    }
                }
                catch { }
            }
            return dir;
        }

        // 磁盘图标缓存路径：文件名包含「像素档位 epoch」，不同界面缩放档位的图标互不覆盖，
        // 升级档位后旧档位自然不再命中（由既有清理逻辑回收）。
        private static string GetIconCacheFilePath(string path, int epoch)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes("hires-v3:e" + epoch + ":" + path.ToLowerInvariant()));
                StringBuilder builder = new StringBuilder(16);
                for (int i = 0; i < 8; i++) builder.Append(hash[i].ToString("x2"));
                return Path.Combine(GetIconCacheDirectory(), builder.ToString() + ".png");
            }
        }

        private static void WriteIconCacheFile(string path, string iconDataUrl, int writeEpoch)
        {
            try
            {
                const string prefix = "data:image/png;base64,";
                if (String.IsNullOrWhiteSpace(iconDataUrl) || !iconDataUrl.StartsWith(prefix, StringComparison.Ordinal)) return;
                byte[] bytes = Convert.FromBase64String(iconDataUrl.Substring(prefix.Length));
                if (bytes.Length == 0) return;
                Directory.CreateDirectory(GetIconCacheDirectory());
                File.WriteAllBytes(GetIconCacheFilePath(path, writeEpoch), bytes);
                TrimIconDiskCache();
            }
            catch { }
        }

        // 写盘后若缓存文件超过 3000 个，删除最旧的 500 个（按 LastWriteTime 排序）。
        private static void TrimIconDiskCache()
        {
            try
            {
                string directory = GetIconCacheDirectory();
                string[] files = Directory.GetFiles(directory, "*.png");
                if (files.Length <= 3000) return;
                Array.Sort(files, delegate(string left, string right)
                {
                    try { return File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right)); }
                    catch { return 0; }
                });
                int removeCount = Math.Min(500, files.Length - 3000);
                for (int i = 0; i < removeCount; i++)
                {
                    try { File.Delete(files[i]); }
                    catch { }
                }
            }
            catch { }
        }

        // 把 args 中的 paths 统一成 string[]（兼容 object[] 与 List<object>）。
        private static string[] ReadStringArray(Dictionary<string, object> values, string key)
        {
            object raw;
            if (!values.TryGetValue(key, out raw) || raw == null) return new string[0];
            List<string> result = new List<string>();
            if (raw is object[])
            {
                foreach (object entry in (object[])raw)
                {
                    if (entry != null) result.Add(Convert.ToString(entry));
                }
            }
            else if (raw is List<object>)
            {
                foreach (object entry in (List<object>)raw)
                {
                    if (entry != null) result.Add(Convert.ToString(entry));
                }
            }
            return result.ToArray();
        }

        // moveIntoCategory：把 paths 移入 桌面\片刻收纳\{分类名}（同名加 " (N)" 后缀）。
        // DeskBox 交互导入语义：目标名用 reserved 集合预解析后，一次 IFileOperation 批量
        // 移动（专用 STA 线程 + 系统进度窗口，可取消），结果按文件系统事实对账。
        private MoveBatchResult MoveIntoCategory(string categoryId, string[] paths)
        {
            return TransferIntoCategory(categoryId, paths, false);
        }

        // 复制收纳（拖放 Ctrl / 右键菜单选择复制）：原文件保留在桌面，副本进分类。
        private MoveBatchResult CopyIntoCategory(string categoryId, string[] paths)
        {
            return TransferIntoCategory(categoryId, paths, true);
        }

        private MoveBatchResult TransferIntoCategory(string categoryId, string[] paths, bool copy)
        {
            MoveBatchResult result = new MoveBatchResult();
            result.Requested = paths == null ? 0 : paths.Length;
            if (IsPanelReadOnly(categoryId)) throw new InvalidOperationException("门户分区只引用文件，不会移动文件");
            string categoryName = FindCategoryName(categoryId);
            string targetRoot = GetManagedCategoryPath(categoryName);
            Directory.CreateDirectory(targetRoot);
            string normalizedTargetRoot = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (paths == null) return result;

            // 1) 过滤 + 目标名预解析（reserved 集合防止批量内同名源撞名）
            List<NativeFileOps.TransferRequest> requests = new List<NativeFileOps.TransferRequest>();
            List<string> requestSources = new List<string>();
            HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string movePath in paths)
            {
                if (String.IsNullOrWhiteSpace(movePath)) { result.Skipped++; continue; }
                string source;
                try { source = Path.GetFullPath(movePath); }
                catch { result.Skipped++; continue; }
                if (!File.Exists(source) && !Directory.Exists(source)) { result.Skipped++; continue; }
                string parent = Path.GetDirectoryName(source);
                string normalizedSource = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!copy && (String.Equals(normalizedSource, normalizedTargetRoot, StringComparison.OrdinalIgnoreCase)
                    || (parent != null && String.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedTargetRoot, StringComparison.OrdinalIgnoreCase))))
                {
                    result.Skipped++;
                    continue;
                }
                string destination = NativeFileOps.GetAvailablePath(Path.Combine(targetRoot, Path.GetFileName(source)), reserved);
                reserved.Add(destination.ToLowerInvariant());
                NativeFileOps.TransferRequest request = new NativeFileOps.TransferRequest();
                request.Source = source;
                request.DestinationFolder = targetRoot;
                request.DestinationName = Path.GetFileName(destination);
                requests.Add(request);
                requestSources.Add(source);
            }
            if (requests.Count == 0) return result;

            // 2) 单次 Shell 批量传输（交互操作带系统进度窗口；owner 取来源分区/设置窗口）
            IntPtr owner = ResolveTransferOwnerWindow(categoryId);
            List<NativeFileOps.TransferResult> results = NativeFileOps.Transfer(requests, owner, true, !copy);

            // 3) 汇总：完成计入 Moved（move 另记撤销历史）；取消项计入 Skipped；失败带原因
            for (int i = 0; i < results.Count; i++)
            {
                NativeFileOps.TransferResult transfer = results[i];
                string source = requestSources[i];
                string destination = transfer.Destination;
                if (transfer.Completed)
                {
                    result.Moved++;
                    InvalidatePathCache(source);
                    InvalidatePathCache(destination);
                    if (!copy) result.History.Add(new HistoryEntry { Source = source, Destination = destination, Name = Path.GetFileName(source) });
                }
                else if (transfer.Aborted)
                {
                    result.Skipped++;
                }
                else
                {
                    result.Failed++;
                    Log((copy ? "复制文件到分类失败：" : "移动文件到分类失败：") + source + " -> " + destination + " " + (transfer.Error ?? "未知错误"));
                    if (result.Failures.Count < 5) result.Failures.Add(Path.GetFileName(source) + "：" + (transfer.Error ?? "未知错误"));
                }
            }
            return result;
        }

        // 传输操作的 owner 窗口：来源分区 → 设置窗口 → 无（系统进度窗仍可正常显示）
        private IntPtr ResolveTransferOwnerWindow(string categoryId)
        {
            PanelWindow panel;
            if (!String.IsNullOrWhiteSpace(categoryId) && panelWindows.TryGetValue(categoryId, out panel) && panel != null && panel.NativeHandle != IntPtr.Zero)
                return panel.NativeHandle;
            if (settingsWindow != null)
            {
                try { return new WindowInteropHelper(settingsWindow).Handle; } catch { }
            }
            return IntPtr.Zero;
        }

        private bool IsPanelReadOnly(string categoryId)
        {
            bool readOnly;
            return panelReadOnly.TryGetValue(categoryId, out readOnly) && readOnly;
        }

        // 通过面板同步数据维护的 id->分类名 映射查找分类。
        private string FindCategoryName(string categoryId)
        {
            string name;
            if (panelNames.TryGetValue(categoryId, out name) && !String.IsNullOrWhiteSpace(name)) return name;
            throw new InvalidOperationException("未找到分类：" + categoryId);
        }

        // 目标重名解析统一走 NativeFileOps.GetAvailablePath（支持批量内 reserved 集合）

        // ---- 门户文件夹 watcher：与桌面/收纳 watcher 分开管理，变化时通知前端重扫 ----
        private void EnsurePortalWatchers(string[] paths)
        {
            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string portalPath in paths)
            {
                if (String.IsNullOrWhiteSpace(portalPath)) continue;
                string full;
                try { full = Path.GetFullPath(portalPath); }
                catch { continue; }
                if (!Directory.Exists(full)) continue;
                wanted.Add(full);
                bool already = false;
                foreach (FileSystemWatcher watcher in portalWatchers)
                {
                    if (String.Equals(watcher.Path, full, StringComparison.OrdinalIgnoreCase)) { already = true; break; }
                }
                if (!already) AddPortalDirectoryWatcher(full);
            }
            // 移除已不在请求列表中的门户 watcher，避免列表无限增长。
            for (int i = portalWatchers.Count - 1; i >= 0; i--)
            {
                FileSystemWatcher watcher = portalWatchers[i];
                if (wanted.Contains(watcher.Path)) continue;
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
                catch { }
                portalWatchers.RemoveAt(i);
            }
        }

        private void AddPortalDirectoryWatcher(string path)
        {
            try
            {
                FileSystemWatcher watcher = new FileSystemWatcher(path);
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.InternalBufferSize = 64 * 1024;
                FileSystemEventHandler handler = OnPortalFileSystemEvent;
                RenamedEventHandler renamed = OnPortalFileSystemRenamed;
                watcher.Changed += handler;
                watcher.Created += handler;
                watcher.Deleted += handler;
                watcher.Renamed += renamed;
                watcher.Error += OnPortalWatcherError;
                watcher.EnableRaisingEvents = true;
                portalWatchers.Add(watcher);
            }
            catch (Exception error)
            {
                Log("创建门户文件监视器失败：" + path + " " + error.Message);
            }
        }

        private void OnPortalFileSystemEvent(object sender, FileSystemEventArgs args)
        {
            SchedulePortalChanged();
        }

        private void OnPortalFileSystemRenamed(object sender, RenamedEventArgs args)
        {
            SchedulePortalChanged();
        }

        private void OnPortalWatcherError(object sender, ErrorEventArgs args)
        {
            Log("门户文件监视器发生错误，已安排完整重扫：" + (args.GetException() == null ? "未知错误" : args.GetException().Message));
            SchedulePortalChanged();
        }

        // watcher 事件线程不固定，统一转到 UI 线程后再通知前端。
        private void SchedulePortalChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    manager.HandleDesktopChanged();
                }));
            }
            catch (Exception error) { Log("调度门户刷新失败：" + error.Message); }
        }

        private void DisposePortalWatchers()
        {
            foreach (FileSystemWatcher watcher in portalWatchers)
            {
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
                catch { }
            }
            portalWatchers.Clear();
        }

        // 面板图标的默认逻辑尺寸（DIP），与 defaultItemLayout.iconSize(54) 保持一致
        private const double DefaultShellIconLogicalSize = 54;

        private string GetShellIcon(string path)
        {
            ShortcutInfo shortcut = ReadShortcut(path);
            string shortcutIcon = GetShortcutIcon(shortcut);
            if (!String.IsNullOrWhiteSpace(shortcutIcon)) return shortcutIcon;

            string resolvedPath = shortcut != null && !String.IsNullOrWhiteSpace(shortcut.TargetPath)
                ? shortcut.TargetPath
                : path;
            if (IsImagePath(resolvedPath))
            {
                string thumbnail = GetShellThumbnail(resolvedPath);
                if (!String.IsNullOrWhiteSpace(thumbnail)) return thumbnail;
            }

            if (!String.Equals(resolvedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                string resolvedIcon = GetShellIconFromPath(resolvedPath);
                if (!String.IsNullOrWhiteSpace(resolvedIcon)) return resolvedIcon;
            }
            return GetShellIconFromPath(path);
        }

        private string GetShellIconFromPath(string path)
        {
            // 目标位图像素 = 默认图标逻辑尺寸 × uiScale × 系统 DPI（本机 150% DPI、uiScale=100 时为 96px）
            int targetPx = ResolveIconPixelSize(DefaultShellIconLogicalSize, currentUiScale, CurrentDpiScale());
            IntPtr large = GetLargeShellIcon(path);
            if (large != IntPtr.Zero)
            {
                try { return IconToDataUrl(large, targetPx); }
                finally { DestroyIcon(large); }
            }

            ShellFileInfo info = new ShellFileInfo();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(ShellFileInfo)), 0x000000100 | 0x000000000);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try { return IconToDataUrl(info.hIcon, targetPx); }
            finally { DestroyIcon(info.hIcon); }
        }

        // 高分辨率图标：面板图标按 iconSize(≈65DIP)×DPI 渲染，32/48px 的 SHGetFileInfo
        // 图标放大后会明显发糊。走 SHGetImageList(SHIL_JUMBO) 取 256px 原生图标，
        // 失败退 SHIL_EXTRALARGE(48px)，由 IconToDataUrl 高质量缩放到目标画布。
        private static IntPtr GetLargeShellIcon(string path)
        {
            try
            {
                ShellFileInfo info = new ShellFileInfo();
                IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(ShellFileInfo)), 0x000004000 | 0x000000000); // SHGFI_SYSICONINDEX
                if (result == IntPtr.Zero) return IntPtr.Zero;
                IntPtr jumbo;
                if (GetIconFromImageList(ShilJumbo, info.iIcon, out jumbo) && jumbo != IntPtr.Zero) return jumbo;
                IntPtr extra;
                if (GetIconFromImageList(ShilExtraLarge, info.iIcon, out extra) && extra != IntPtr.Zero) return extra;
            }
            catch { }
            return IntPtr.Zero;
        }

        private static bool GetIconFromImageList(int imageListId, int iconIndex, out IntPtr icon)
        {
            icon = IntPtr.Zero;
            Guid imageListIid = new Guid("46EB5926-582E-4017-9FDF-E8998D80B5F5"); // IImageList
            IImageList list;
            if (SHGetImageList(imageListId, ref imageListIid, out list) != 0 || list == null) return false;
            try
            {
                return list.GetIcon(iconIndex, 1 /*ILD_TRANSPARENT*/, ref icon) == 0;
            }
            catch
            {
                if (icon != IntPtr.Zero) { DestroyIcon(icon); icon = IntPtr.Zero; }
                return false;
            }
        }

        // 按目标像素生成 PNG 数据 URL：targetPx 会被夹到 [48,256] 并对齐到 16 的倍数，
        // 内边距取 targetPx/16（至少 2px），保持原 96→84 的 12.5% 内缩比例。
        private static string IconToDataUrl(IntPtr iconHandle, int targetPx)
        {
            if (iconHandle == IntPtr.Zero) return null;
            int canvas = targetPx < 48 ? 48 : (targetPx > 256 ? 256 : targetPx);
            canvas = ((canvas + 15) / 16) * 16;
            if (canvas > 256) canvas = 256;
            if (canvas < 48) canvas = 48;
            int padding = Math.Max(2, canvas / 16);
            int inner = canvas - padding * 2;
            using (System.Drawing.Icon icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(iconHandle).Clone())
            using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(canvas, canvas, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (MemoryStream stream = new MemoryStream())
            {
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.Clear(System.Drawing.Color.Transparent);
                    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    graphics.DrawIcon(icon, new System.Drawing.Rectangle(padding, padding, inner, inner));
                }
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            }
        }

        // 计算某逻辑尺寸在指定 UI 缩放与 DPI 下需要的位图像素（向上取 16 倍数，夹到 [48,256]）
        internal static int ResolveIconPixelSize(double logicalSize, double uiScalePercent, double dpiScale)
        {
            // 任何非法输入都回落到安全默认值：不抛异常、不返回 0
            if (Double.IsNaN(logicalSize) || Double.IsInfinity(logicalSize) || logicalSize <= 0) logicalSize = 54;
            if (Double.IsNaN(uiScalePercent) || Double.IsInfinity(uiScalePercent) || uiScalePercent <= 0) uiScalePercent = 100;
            if (Double.IsNaN(dpiScale) || Double.IsInfinity(dpiScale) || dpiScale <= 0) dpiScale = 1.0;

            double raw = logicalSize * (uiScalePercent / 100.0) * dpiScale;
            if (Double.IsNaN(raw) || Double.IsInfinity(raw) || raw <= 0) raw = 48;

            double steps = Math.Ceiling(raw / 16.0);
            if (steps < 3) steps = 3;    // 48px 下限
            if (steps > 16) steps = 16;  // 256px 上限
            return (int)(steps * 16);
        }

        private static bool IsImagePath(string path)
        {
            string extension = Path.GetExtension(path);
            if (String.IsNullOrWhiteSpace(extension)) return false;
            switch (extension.ToLowerInvariant())
            {
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp":
                case ".webp": case ".tif": case ".tiff": case ".ico": case ".heic":
                    return true;
                default: return false;
            }
        }

        private static ShortcutInfo ReadShortcut(string path)
        {
            if (!String.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) return null;
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                object target = shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                object iconLocation = shortcut.GetType().InvokeMember("IconLocation", System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                string targetPath = Environment.ExpandEnvironmentVariables(Convert.ToString(target));
                if (!String.IsNullOrWhiteSpace(targetPath) && !Path.IsPathRooted(targetPath))
                {
                    object workingDirectory = shortcut.GetType().InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                    string basePath = Environment.ExpandEnvironmentVariables(Convert.ToString(workingDirectory));
                    if (!String.IsNullOrWhiteSpace(basePath)) targetPath = Path.GetFullPath(Path.Combine(basePath, targetPath));
                }
                if (!File.Exists(targetPath) && !Directory.Exists(targetPath)) targetPath = null;
                return new ShortcutInfo { TargetPath = targetPath, IconLocation = Convert.ToString(iconLocation) };
            }
            catch { }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
            return null;
        }

        private string GetShortcutIcon(ShortcutInfo shortcut)
        {
            if (shortcut == null || String.IsNullOrWhiteSpace(shortcut.IconLocation)) return null;
            string iconPath = shortcut.IconLocation.Trim();
            int iconIndex = 0;
            int comma = iconPath.LastIndexOf(',');
            int parsedIndex;
            if (comma > 0 && Int32.TryParse(iconPath.Substring(comma + 1).Trim(), out parsedIndex))
            {
                iconIndex = parsedIndex;
                iconPath = iconPath.Substring(0, comma);
            }
            iconPath = Environment.ExpandEnvironmentVariables(iconPath.Trim().Trim('"'));
            if (!File.Exists(iconPath)) return null;

            IntPtr[] large = new IntPtr[1];
            IntPtr[] small = new IntPtr[1];
            try
            {
                if (ExtractIconEx(iconPath, iconIndex, large, small, 1) == 0) return null;
                IntPtr handle = large[0] != IntPtr.Zero ? large[0] : small[0];
                int targetPx = ResolveIconPixelSize(DefaultShellIconLogicalSize, currentUiScale, CurrentDpiScale());
                return IconToDataUrl(handle, targetPx);
            }
            catch { return null; }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero && small[0] != large[0]) DestroyIcon(small[0]);
            }
        }

        private static string GetShellThumbnail(string path)
        {
            IntPtr bitmapHandle = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                IShellItemImageFactory factory;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
                // 192px：覆盖 65DIP 图标在 150% DPI 下的显示需求（72px 会放大发糊）
                ShellSize size = new ShellSize { Width = 192, Height = 192 };
                factory.GetImage(size, ShellImageFlags.ThumbnailOnly | ShellImageFlags.BigEnough | ShellImageFlags.ScaleUp, out bitmapHandle);
                if (bitmapHandle == IntPtr.Zero) return null;
                using (System.Drawing.Bitmap bitmap = System.Drawing.Bitmap.FromHbitmap(bitmapHandle))
                using (MemoryStream stream = new MemoryStream())
                {
                    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
                }
            }
            catch { return null; }
            finally
            {
                if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
            }
        }

        // 面板事件直连 Manager（不再经过 webview）：PanelWindow 调用方无需改动
        internal void PostManagerEvent(string name, object value)
        {
            manager.HandlePanelEvent(name, value);
        }

        // =============================================================
        // IManagerHost：Manager 的全部外部 IO（Manager 在后台线程调用，
        // 文件类操作线程安全；UI 操作一律经 RunOnUi 回到 UI 线程）
        // =============================================================
        string IManagerHost.DesktopPath
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        }

        double IManagerHost.WorkAreaWidth
        {
            get { return SystemParameters.WorkArea.Width; }
        }

        double IManagerHost.WorkAreaHeight
        {
            get { return SystemParameters.WorkArea.Height; }
        }

        List<ManagerEntry> IManagerHost.ScanDirectory(string path, bool refreshIcons)
        {
            // 复用现有扫描实现（含图标缓存/桌面 watcher），把字典条目转换为 ManagerEntry
            return ToManagerEntries(ScanDirectory(path, refreshIcons));
        }

        List<ManagerEntry> IManagerHost.ReadDirectory(string path)
        {
            return ToManagerEntries(ReadDirectory(path));
        }

        // 字典条目 → ManagerEntry（ModifiedAt 已是 Unix 毫秒；readDirectory 无这些字段时取默认值）
        private static List<ManagerEntry> ToManagerEntries(List<Dictionary<string, object>> entries)
        {
            List<ManagerEntry> result = new List<ManagerEntry>(entries.Count);
            foreach (Dictionary<string, object> entry in entries)
            {
                ManagerEntry item = new ManagerEntry();
                item.Name = entry.ContainsKey("entry") ? Convert.ToString(entry["entry"]) : "";
                item.Type = entry.ContainsKey("type") ? Convert.ToString(entry["type"]) : "FILE";
                object size;
                item.Size = entry.TryGetValue("size", out size) ? Convert.ToDouble(size) : 0;
                object modified;
                item.ModifiedAt = entry.TryGetValue("modifiedAt", out modified) ? Convert.ToDouble(modified) : double.NaN;
                item.IconUrl = entry.ContainsKey("iconUrl") ? Convert.ToString(entry["iconUrl"]) : null;
                object hidden;
                item.Hidden = entry.TryGetValue("hidden", out hidden) && Convert.ToBoolean(hidden);
                result.Add(item);
            }
            return result;
        }

        bool IManagerHost.PathExists(string path)
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        void IManagerHost.CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        void IManagerHost.Move(string source, string destination)
        {
            MoveItem(source, destination);
            InvalidatePathCache(source);
            InvalidatePathCache(destination);
        }

        void IManagerHost.OpenPath(string path)
        {
            OpenItem(path);
        }

        string IManagerHost.PickFolder()
        {
            return PickFolderCore();
        }

        // 文件夹选择框（设置窗口与 Manager 共用）；取消返回空串
        private static string PickFolderCore()
        {
            using (Forms.FolderBrowserDialog dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择要镜像到桌面的文件夹";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() == Forms.DialogResult.OK) return dialog.SelectedPath;
            }
            return "";
        }

        // 事件通知：设置窗口当前不消费事件，忽略（Manager 的 toast 等因此静默丢弃）
        void IManagerHost.PostEvent(string name, object value)
        {
        }

        void IManagerHost.SetPanelsVisible(bool visible)
        {
            SetPanelsVisible(visible);
        }

        void IManagerHost.SetClickThrough(bool enabled)
        {
            SetClickThrough(enabled);
        }

        // 模态确认框（组织收纳/撤销前询问）
        bool IManagerHost.Confirm(string message)
        {
            bool isDark = String.Equals(currentTheme, "dark", StringComparison.OrdinalIgnoreCase);
            Color accent = Color.FromRgb(52, 120, 246);
            if (!String.IsNullOrWhiteSpace(currentAccent))
            {
                try { accent = (Color)ColorConverter.ConvertFromString(currentAccent); } catch { }
            }
            return ConfirmDialog.ShowConfirm(this, "片刻收纳", message, null, "确定", "取消", false, isDark, accent);
        }

        MoveBatchResult IManagerHost.MoveIntoCategory(string categoryId, string[] paths)
        {
            return MoveIntoCategory(categoryId, paths);
        }

        MoveBatchResult IManagerHost.CopyIntoCategory(string categoryId, string[] paths)
        {
            return CopyIntoCategory(categoryId, paths);
        }

        void IManagerHost.WatchPortalFolders(string[] paths)
        {
            EnsurePortalWatchers(paths);
        }

        // Manager 回调统一回到 UI 线程（关闭阶段 Dispatcher 不可用时同步兜底）
        void IManagerHost.RunOnUi(Action action)
        {
            try
            {
                Dispatcher.BeginInvoke(action);
            }
            catch
            {
                try { action(); } catch { }
            }
        }

        void IManagerHost.Log(string message)
        {
            Log(message);
        }

        // 配置路径与 Manager.cs 的 GetConfigPath 保持一致（%LocalAppData%\PivkeyOrganizer\config.json）
        private static string GetConfigPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "PivkeyOrganizer", "config.json");
        }

        private string ExportConfigFile()
        {
            using (Forms.SaveFileDialog dialog = new Forms.SaveFileDialog())
            {
                dialog.Title = "导出片刻收纳配置";
                dialog.Filter = "片刻收纳配置 (*.json)|*.json|所有文件 (*.*)|*.*";
                dialog.FileName = "pivkey-organizer-config.json";
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return "";
                try
                {
                    File.WriteAllText(dialog.FileName, manager.SerializeConfig(), new UTF8Encoding(false));
                    return dialog.FileName;
                }
                catch (Exception error)
                {
                    Log("配置导出失败：" + error.Message);
                    return "";
                }
            }
        }

        private string ImportConfigFile()
        {
            using (Forms.OpenFileDialog dialog = new Forms.OpenFileDialog())
            {
                dialog.Title = "导入片刻收纳配置";
                dialog.Filter = "片刻收纳配置 (*.json)|*.json|所有文件 (*.*)|*.*";
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return "";
                try
                {
                    Dictionary<string, object> config = serializer.DeserializeObject(File.ReadAllText(dialog.FileName)) as Dictionary<string, object>;
                    if (config == null) throw new InvalidDataException("配置文件格式无效");
                    manager.ApplyConfig(config);
                    WriteConfigFromManager();
                    return dialog.FileName;
                }
                catch (Exception error)
                {
                    Log("配置导入失败：" + error.Message);
                    return "";
                }
            }
        }

        // 把 Manager 当前配置立即写盘（迁移页 importConfig 后使用）：Manager 自身 180ms
        // 防抖写盘在随后的 Initialize 重新读盘前可能尚未发生，需保证迁移数据立即落盘。
        private void WriteConfigFromManager()
        {
            try
            {
                string path = GetConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, manager.SerializeConfig(), new UTF8Encoding(false));
            }
            catch (Exception error)
            {
                Log("迁移配置写盘失败：" + error.Message);
            }
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint infoSize, uint flags);
        [DllImport("shell32.dll")] private static extern int SHGetImageList(int imageListId, ref Guid interfaceId, out IImageList imageList);
        private const int ShilExtraLarge = 0x2;
        private const int ShilJumbo = 0x4;

        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998D80B5F5")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            // vtable 顺序声明：只用到 GetIcon，但前面的方法必须按接口顺序占位
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
            [PreserveSig] int Draw(ref IMAGELISTDRAWPARAMS pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IMAGELISTDRAWPARAMS
        {
            public IntPtr himl;
            public int i;
            public IntPtr hdcDst;
            public int x, y, cx, cy;
            public int xBitmap, yBitmap;
            public uint rgbBk, rgbFg, fStyle, dwRop;
            public int fState, Frame, CrEffect;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string fileName, int iconIndex, IntPtr[] largeIcons, IntPtr[] smallIcons, uint iconCount);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);
        // 每显示器 DPI：用于计算图标位图需要的物理像素（Win10 1607+ 可用）
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, int minimumWorkingSetSize, int maximumWorkingSetSize);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellFileInfo { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
        [StructLayout(LayoutKind.Sequential)] private struct ShellSize { public int Width; public int Height; }
        [Flags] private enum ShellImageFlags : uint { ThumbnailOnly = 0x8, BigEnough = 0x1, ScaleUp = 0x100 }
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IShellItemImageFactory
        {
            void GetImage(ShellSize size, ShellImageFlags flags, out IntPtr bitmap);
        }
        private void InvalidatePathCache(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            lock (shellIconCacheLock)
            {
                scanEntryCache.Remove(path);
                shellIconCache.Remove(path);
            }
        }

        // 清空全部 shell 图标内存缓存并切换磁盘缓存版本前缀，使旧像素档位的图标整体失效。
        // 触发时机：界面缩放变化（DIP 尺寸变了 → 需要的位图像素档位也变了）。
        // 只失效不重扫：下一次扫描会按新档位重新生成并按需写盘。
        private void InvalidateShellIconCache()
        {
            lock (shellIconCacheLock)
            {
                shellIconCache.Clear();
                scanEntryCache.Clear();
            }
            iconCacheEpoch++;
        }

        // 磁盘图标缓存的版本号：随界面缩放档位递增，让不同档位的图标互不覆盖。
        private int iconCacheEpoch;

        private sealed class IconCacheItem
        {
            public string Data;
            public long WriteTimeUtcTicks;
            public long LastAccessTicks;
        }

        private sealed class ShortcutInfo
        {
            public string TargetPath;
            public string IconLocation;
        }

        private sealed class ScanCacheItem
        {
            public string EntryName;
            public string Type;
            public long WriteTimeUtcTicks;
            public long Size;
            public bool Hidden;
            public string IconUrl;
        }

        internal sealed class RegionRect { public int Left; public int Top; public int Right; public int Bottom; public int Radius; }
    }

}
