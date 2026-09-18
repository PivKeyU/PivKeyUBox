// =====================================================================
// Manager.cs —— 「片刻收纳」原生核心（纯逻辑大脑）
// ---------------------------------------------------------------------
// 职责：把原先跑在 React 管理器 webview 里的全部业务逻辑移植到 C#：
//   配置加载/保存（%LocalAppData%\PivkeyOrganizer\config.json）
//   桌面扫描编排（含受管收纳目录、文件夹门户、引用钉选、隐藏过滤）
//   自动收纳 / 自动扫描定时、组织稳定性跟踪、撤销收纳
//   面板负载构建（主题色计算、Magic 分区色、revision、图标去重）
//   面板/托盘/桌面变更事件处理、自适应布局与对齐重排
// 约定：
//   * 宿主在 UI 线程调用公共方法；Manager 内部定时器/后台任务
//     自行管理，回调经 IManagerHost.RunOnUi 回到 UI 线程。
//   * 不 new 任何 Window/UI 控件，全部 IO 经 IManagerHost 抽象。
//   * C# 5 语法（csc /langversion:5），中文注释，UTF-8 无 BOM。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace PivkeyOrganizer
{
    internal sealed class MoveBatchResult
    {
        public int Requested;
        public int Moved;
        public int Skipped;
        public int Failed;
        public List<string> Failures = new List<string>();
        public List<HistoryEntry> History = new List<HistoryEntry>();
    }

    // 便签待办条目（Todo Item）
    internal sealed class TodoItemData
    {
        public string Id;
        public string Text;
        public bool Done;
        public double CreatedAt;
    }

    // 桌面便签（Sticky Note / Paper）
    internal sealed class NoteData
    {
        public string Id;
        public string Title;
        public string Mode;          // "todo" | "note"
        public string Color;         // "#ffffff", "#fef9d9", "#e8f5e9", "#e3f2fd", "#fce4ec", "#1e1e24"
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public bool Pinned;
        public bool Collapsed;
        public string NoteContent;
        public List<TodoItemData> Todos = new List<TodoItemData>();

        public NoteData Clone()
        {
            NoteData copy = new NoteData();
            copy.Id = Id;
            copy.Title = Title;
            copy.Mode = Mode;
            copy.Color = Color;
            copy.X = X;
            copy.Y = Y;
            copy.Width = Width;
            copy.Height = Height;
            copy.Pinned = Pinned;
            copy.Collapsed = Collapsed;
            copy.NoteContent = NoteContent;
            if (Todos != null)
            {
                foreach (TodoItemData t in Todos)
                {
                    copy.Todos.Add(new TodoItemData
                    {
                        Id = t.Id,
                        Text = t.Text,
                        Done = t.Done,
                        CreatedAt = t.CreatedAt
                    });
                }
            }
            return copy;
        }
    }

    // =================================================================
    // 宿主接口：Manager 的全部外部 IO 都通过该接口完成。
    // =================================================================
    internal interface IManagerHost
    {
        // 桌面目录（getDesktopPath 语义）
        string DesktopPath { get; }

        // 当前工作区尺寸（逻辑像素，等价 window.innerWidth/innerHeight）
        double WorkAreaWidth { get; }
        double WorkAreaHeight { get; }

        // 扫描目录：返回完整条目（等价宿主 scanDirectory，refreshIcons 控制图标刷新）
        List<ManagerEntry> ScanDirectory(string path, bool refreshIcons);

        // 仅列目录条目（不取图标，等价宿主 readDirectory；用于受管根/门户根）
        List<ManagerEntry> ReadDirectory(string path);

        bool PathExists(string path);        // getStats 语义：存在返回 true
        void CreateDirectory(string path);
        void Move(string source, string destination);
        void OpenPath(string path);
        string PickFolder();                 // 文件夹选择框，取消返回空串

        void SyncPanels(PanelSyncData[] panels);     // 创建/更新面板窗口
        void SyncNotes(List<NoteData> notes, bool noteCapsuleMode, bool desktopContextMenu); // 创建/更新便签窗口
        void PostEvent(string name, object value);   // 通知设置窗口等（toast 等）
        void SetPanelsVisible(bool visible);         // 显示/隐藏全部分区
        void SetClickThrough(bool enabled);          // 切换鼠标穿透（含面板联动）
        bool Confirm(string message);                // 模态确认框（组织/撤销前询问）
        MoveBatchResult MoveIntoCategory(string categoryId, string[] paths); // 拖拽收纳（宿主已有语义）
        MoveBatchResult CopyIntoCategory(string categoryId, string[] paths); // 复制收纳（原文件保留在桌面）
        void WatchPortalFolders(string[] paths);     // 注册/更新门户目录 watcher

        void RunOnUi(Action action);         // 把回调 marshal 回宿主 UI 线程
        void Log(string message);            // 追加写日志，异常静默
    }

    // 扫描条目（字段对应宿主 scanDirectory 返回的 entry/size/hidden/modifiedAt/iconUrl）
    internal sealed class ManagerEntry
    {
        public string Name;
        public string Type;          // "FILE" | "DIRECTORY"
        public double Size;
        public double ModifiedAt;    // Unix 毫秒（UTC），非法时为 NaN
        public string IconUrl;       // data: 图标，可能为 null
        public bool Hidden;
    }

    // 内部桌面项（对应前端 DesktopItem）
    internal sealed class ManagerItem
    {
        public string Id;
        public string Name;
        public string Path;
        public string Extension;
        public string Kind;          // shortcut/folder/document/image/media/archive/other
        public double Size;
        public string ModifiedAt;    // ISO 字符串（与前端 toISOString 同格式）
        public string CategoryId;
        public bool Managed;         // 受管/门户项永不参与自动收纳
        public string IconUrl;
    }

    // 分类（对应前端 Category，normalize 后）
    internal sealed class CategoryData
    {
        public string Id;
        public string Name;
        public string Icon;
        public string Color;
        public List<string> Extensions = new List<string>();
        public bool AcceptsFolders;
        public string PortalPath;    // 可选：门户目录
    }

    // 偏好（对应前端 AppPreferences，normalize 后）
    internal sealed class PreferencesData
    {
        public List<CategoryData> Categories = new List<CategoryData>();
        public bool AutomaticScan;
        public bool AutomaticOrganize;
        public int OrganizeDelaySeconds = 10;
        public string OrganizeMode = "move";                       // move | reference
        public bool MagicColor;
        public Dictionary<string, List<string>> ReferencePins = new Dictionary<string, List<string>>();
        public bool ShowHiddenFiles;
        public bool CompactView;
        public bool CapsuleMode;
        public bool NoteCapsuleMode;
        public bool DesktopContextMenu = true;
        public int GlassOpacity = 88;
        public string Material = "acrylic";                        // acrylic | solid（真亚克力开关，DeskBox 式系统材质）
        public string Theme = "light";                             // light | dark | system
        public string ColorScheme = "white";                        // white | warm | ink | forest | rose | custom
        public string CustomColor = "#3478f6";
        public string CustomSurfaceColor = "#ffffff";
        public int UiScale = 100;
        public bool ShowExtensions;
        public string LabelPosition = "bottom";                  // bottom | right
        public bool AutoHide;
        public int AutoHideDelaySeconds = 2;
        public List<OrganizationRuleData> Rules = new List<OrganizationRuleData>();
        public List<string> Favorites = new List<string>();
        public List<string> RecentItems = new List<string>();
        public List<string> PinnedItems = new List<string>();
        public List<WorkspaceData> Workspaces = new List<WorkspaceData>();
        public string ActiveWorkspaceId = "";
    }

    internal sealed class OrganizationRuleData
    {
        public string Id;
        public string Name;
        public bool Enabled = true;
        public string Match = "extension";                       // extension | name | path
        public string Pattern = "";
        public string CategoryId;
        public int Priority = 1;
    }

    internal sealed class WorkspaceData
    {
        public string Id;
        public string Name;
        public Dictionary<string, PositionData> Positions = new Dictionary<string, PositionData>();
        public Dictionary<string, SizeData> Sizes = new Dictionary<string, SizeData>();
        public List<string> Collapsed = new List<string>();
        public List<string> Pinned = new List<string>();
        public Dictionary<string, string> ViewModes = new Dictionary<string, string>();
        public Dictionary<string, string> SortModes = new Dictionary<string, string>();
        public Dictionary<string, ItemLayoutData> ItemLayouts = new Dictionary<string, ItemLayoutData>();
        public string AdaptivePreset = "manual";
        public string FolderAlignment = "manual";
    }

    // 面板条目布局（对应前端 PanelItemLayout；构造即默认值）
    internal sealed class ItemLayoutData
    {
        public double ItemSize = 76;
        public double IconSize = 54;
        public double Gap = 6;
        public string Alignment = "left";     // left | center | right
        public int Columns = 0;
        public bool ShowLabels = true;
        public double LabelSize = 10;
    }

    // 组织移动（对应前端 OrganizationMove）
    internal sealed class OrganizationMove
    {
        public ManagerItem Item;
        public string Source;
        public string Destination;
        public string FolderPath;      // 目标分类文件夹（构建时已确定，执行不依赖配置）
        public string CategoryId;
        public string Status;          // pending | moved | failed
        public string Error;
    }

    // 撤销历史（对应前端 OrganizationHistoryEntry）
    internal sealed class HistoryEntry
    {
        public string Source;
        public string Destination;
        public string Name;
    }

    // 位置 / 尺寸
    internal sealed class PositionData { public double X; public double Y; }
    internal sealed class SizeData { public double Width; public double Height; }

    // 面板占地矩形（纯数值；Manager 不依赖 WPF 类型）
    internal struct PanelRect
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Width;
        public readonly double Height;

        public PanelRect(double x, double y, double width, double height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
    }

    // 组织候选稳定性跟踪（对应前端 organizeCandidates 值）
    internal sealed class CandidateState
    {
        public string Signature;
        public long StableSince;     // Unix 毫秒
        public CandidateState(string signature, long stableSince) { Signature = signature; StableSince = stableSince; }
    }

    // 面板 revision（对应前端 panelRevisions 值）
    internal sealed class PanelRevision
    {
        public string Key;
        public int Revision;
        public PanelRevision(string key, int revision) { Key = key; Revision = revision; }
    }

    // =================================================================
    // 布局类型（对应 src/state/layout.ts 与 src/utils/panelLayout.ts）
    // =================================================================
    internal sealed class ZonePosition { public double X; public double Y; }
    internal sealed class ZoneSize { public double Width; public double Height; }

    internal sealed class PanelViewport
    {
        public double Width;
        public double Height;
        public double? Margin;
        public double? Top;
        public double? Bottom;
        public double? Gap;
    }

    internal sealed class NormalizedViewport
    {
        public double Width, Height, Margin, Top, Bottom, Gap, AvailableWidth, AvailableHeight;
    }

    internal sealed class GridShape
    {
        public int Columns, Rows;
        public double PanelWidth, PanelHeight;
    }

    internal sealed class LayoutResult
    {
        public readonly Dictionary<string, ZonePosition> Positions = new Dictionary<string, ZonePosition>();
        public readonly Dictionary<string, ZoneSize> Sizes = new Dictionary<string, ZoneSize>();
    }

    // =================================================================
    // Manager：纯逻辑核心
    // =================================================================
    internal sealed class Manager : IDisposable
    {
        // ---- 常量（与 defaults.ts / panelLayout.ts / theme.ts / magicColor.ts 逐一核对） ----
        private const int DefaultGlassOpacity = 88;
        private const string DefaultCustomColor = "#3478f6";
        private const string DefaultCustomSurfaceColor = "#ffffff";
        private const string FallbackCategoryColor = "#9ecfc0";
        private const string FallbackCategoryIcon = "chiikawa-idle";
        private const string UncategorizedId = "uncategorized";
        private const string ManagedRootName = "片刻收纳";
        private const string LegacyRootName = "轻屿收纳";
        private const string OtherCategoryName = "其他";

        private const double PanelMinWidth = 230;
        private const double PanelMinHeight = 150;
        private const double PanelLayoutGap = 0;

        private const int ConfigSaveDebounceMs = 180;     // 配置防抖写盘
        private const int PanelSyncDebounceMs = 24;       // 面板同步防抖（对齐前端）
        private const int DesktopChangeDebounceMs = 280;  // 桌面变更防抖重扫
        private const int ResizeReflowDebounceMs = 160;   // 尺寸变更防抖重排
        private const int MaxCategoryCount = 100;

        // 主题色板（theme.ts schemeColors）
        private static readonly Dictionary<string, SchemeEntry> SchemeColors = new Dictionary<string, SchemeEntry>
        {
            { "white", new SchemeEntry("#3478f6", new int[] { 255, 255, 255 }) },
            { "warm",  new SchemeEntry("#e98687", new int[] { 255, 249, 240 }) },
            { "ink",   new SchemeEntry("#7a8fab", new int[] { 246, 248, 252 }) },
            { "forest",new SchemeEntry("#78a68d", new int[] { 241, 249, 244 }) },
            { "rose",  new SchemeEntry("#d887a0", new int[] { 253, 242, 246 }) },
        };

        // Magic 分区色（magicColor.ts kindToColor）
        private static readonly Dictionary<string, string> KindToColor = new Dictionary<string, string>
        {
            { "shortcut", "#f4c968" },
            { "folder",   "#9ecfc0" },
            { "document", "#a9cbe4" },
            { "image",    "#c5b7df" },
            { "media",    "#5856d6" },
            { "archive",  "#dfb07b" },
            { "other",    "#3478f6" },
        };

        private sealed class SchemeEntry
        {
            public string Accent;
            public int[] Surface;
            public SchemeEntry(string accent, int[] surface) { Accent = accent; Surface = surface; }
        }

        // 扩展名集合（classifier.ts）
        private static readonly HashSet<string> ShortcutExtensions = new HashSet<string>(StringComparer.Ordinal) { "lnk", "url", "appref-ms" };
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.Ordinal) { "jpg", "jpeg", "png", "gif", "webp", "svg", "heic", "bmp" };
        private static readonly HashSet<string> MediaExtensions = new HashSet<string>(StringComparer.Ordinal) { "mp3", "wav", "m4a", "flac", "mp4", "mov", "mkv", "avi" };
        private static readonly HashSet<string> ArchiveExtensions = new HashSet<string>(StringComparer.Ordinal) { "zip", "rar", "7z", "tar", "gz" };

        // 分类名校验（categoryValidation.ts）
        private static readonly Regex InvalidWindowsNameCharacters = new Regex("[<>:\"/\\\\|?*\u0000-\u001f]", RegexOptions.CultureInvariant);
        private static readonly Regex ReservedWindowsNames = new Regex("^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex HexColorPattern = new Regex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);
        private static readonly Regex NameTokenRegex = new Regex("\\d+|\\D+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly CompareInfo ZhCompareInfo = CreateZhCompareInfo();

        private static CompareInfo CreateZhCompareInfo()
        {
            try { return new CultureInfo("zh-CN").CompareInfo; }
            catch { return CultureInfo.InvariantCulture.CompareInfo; }
        }

        // ---- 宿主与序列化 ----
        private readonly IManagerHost host;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly object stateLock = new object();   // 全部状态共享锁

        // ---- 运行时状态 ----
        private PreferencesData preferences = new PreferencesData();
        private readonly Dictionary<string, PositionData> positions = new Dictionary<string, PositionData>();
        private readonly Dictionary<string, SizeData> sizes = new Dictionary<string, SizeData>();
        private List<string> collapsed = new List<string>();
        // 避让还原记录：From = 被挤走前的原位，To = 被挤到的位置
        private sealed class PushbackRecord { public double FromX, FromY, ToX, ToY; }
        /// 展开推挤记录：expanderId → 被挤面板 id → 原位/落位。折叠时按此还原。
        private readonly Dictionary<string, Dictionary<string, PushbackRecord>> expandPushback = new Dictionary<string, Dictionary<string, PushbackRecord>>(StringComparer.Ordinal);
        private List<string> pinned = new List<string>();
        private readonly Dictionary<string, string> viewModes = new Dictionary<string, string>();
        private readonly Dictionary<string, string> sortModes = new Dictionary<string, string>();
        private readonly Dictionary<string, ItemLayoutData> itemLayouts = new Dictionary<string, ItemLayoutData>();
        private string adaptivePreset = "manual";   // manual | balanced | grid | columns | corners | right-dock
        private string folderAlignment = "manual";  // manual | left | center | right
        private List<HistoryEntry> organizationHistory = new List<HistoryEntry>();
        private readonly List<List<HistoryEntry>> operationHistory = new List<List<HistoryEntry>>();
        private readonly List<List<HistoryEntry>> redoHistory = new List<List<HistoryEntry>>();
        private const int MaxOperationHistory = 30;

        // ---- 扫描 / 组织状态 ----
        private List<ManagerItem> scanItems = new List<ManagerItem>();
        private bool scanInFlight;
        private bool refreshQueued;
        private bool organizingInFlight;
        private bool interactionActive;                 // 模态拖拽等交互中（暂停自动收纳）
        private bool clickThrough;
        private Dictionary<string, CandidateState> organizeCandidates = new Dictionary<string, CandidateState>();

        // ---- 面板同步状态 ----
        private readonly Dictionary<string, PanelRevision> panelRevisions = new Dictionary<string, PanelRevision>();
        private readonly Dictionary<string, string> sentIcons = new Dictionary<string, string>();
        private string lastSyncKey = "";

        // ---- 便签状态 ----
        private readonly List<NoteData> notes = new List<NoteData>();

        // ---- 定时器（System.Threading.Timer，禁止 WPF DispatcherTimer） ----
        private Timer autoTimer;         // 自动扫描/收纳周期
        private Timer saveTimer;         // 配置防抖写盘
        private Timer syncTimer;         // 面板同步防抖
        private Timer desktopRescanTimer;// 桌面变更 280ms 防抖
        private Timer reflowTimer;       // 尺寸变更 160ms 防抖

        private bool disposed;

        // 主配置损坏后从快照恢复时记录提示，供设置窗口读取并向用户显式报告
        private string configRecoveryNotice = "";

        public Manager(IManagerHost host)
        {
            this.host = host;
        }

        // =============================================================
        // 生命周期
        // =============================================================
        internal void Initialize()
        {
            // 加载配置（不存在则默认）→ 首次扫描 → 同步面板 → 启动定时器
            LoadConfig();
            try { SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; }
            catch (Exception ex) { host.Log("注册显示变更监听失败：" + ex.Message); }
            RestartAutoTimer();
            host.WatchPortalFolders(GetPortalPaths());
            RunScan(true);
            RequestNotesSync();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs args)
        {
            HandleResize();
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed) return;
                disposed = true;
            }
            try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; }
            catch { }
            lock (stateLock)
            {
                DisposeTimer(ref autoTimer);
                DisposeTimer(ref saveTimer);
                DisposeTimer(ref syncTimer);
                DisposeTimer(ref desktopRescanTimer);
                DisposeTimer(ref reflowTimer);
            }
            SaveConfigNow();   // 立即写盘
        }

        private static void DisposeTimer(ref Timer timer)
        {
            if (timer == null) return;
            try { timer.Dispose(); }
            catch { }
            timer = null;
        }

        // =============================================================
        // 配置：加载 / 归一化 / 保存
        // =============================================================
        private static string GetConfigPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "PivkeyOrganizer", "config.json");
        }

        private static string GetConfigBackupPath()
        {
            return GetConfigPath() + ".bak";
        }

        // 读取单个配置文件；损坏/缺失返回 null
        private static Dictionary<string, object> TryReadConfig(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                return serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        private void LoadConfig()
        {
            // 先读主配置；损坏时回退上次成功写入的快照（config.json.bak），
            // 与 DeskBox 的 resilient snapshots 一致：配置损坏不静默回退默认值。
            string path = GetConfigPath();
            Dictionary<string, object> config = TryReadConfig(path);
            if (config == null)
            {
                config = TryReadConfig(GetConfigBackupPath());
                if (config != null)
                {
                    configRecoveryNotice = "主配置文件损坏，已从备份快照恢复上一次的设置。";
                    host.Log("主配置损坏，已从 config.json.bak 恢复");
                }
                else if (File.Exists(path))
                {
                    configRecoveryNotice = "配置文件无法读取，已重置为默认设置。可在“高级”页导入之前的配置备份。";
                    host.Log("主配置与快照均无法读取，使用默认配置");
                }
            }
            ApplyConfig(config);
        }

        /// 取出并清空快照恢复提示（供设置窗口 loadConfig 时附带返回）
        internal string ConsumeConfigRecoveryNotice()
        {
            string notice = configRecoveryNotice;
            configRecoveryNotice = "";
            return notice;
        }

        /// 设置窗口 saveConfig 入口：解析→normalize→存盘→重新同步面板/重排布局/更新 watcher。
        /// config 为 null 时全部使用默认值（首次启动）。
        internal void ApplyConfig(Dictionary<string, object> config)
        {
            bool reflowShouldExpand = false;
            lock (stateLock)
            {
                if (disposed) return;
                Dictionary<string, object> cfg = config ?? new Dictionary<string, object>();
                object rawPrefs;
                Dictionary<string, object> prefs = cfg.TryGetValue("preferences", out rawPrefs) ? rawPrefs as Dictionary<string, object> : null;
                bool previousCapsuleMode = preferences.CapsuleMode;   // 记录切换前的胶囊模式（判断是否发生切换）
                int previousUiScale = preferences.UiScale;
                string previousAdaptivePreset = adaptivePreset;
                string previousFolderAlignment = folderAlignment;
                preferences = NormalizePreferences(prefs ?? new Dictionary<string, object>());

                positions.Clear();
                MergePositions(positions, AsDict(Get(cfg, "positions")));
                bool hadSavedPositions = positions.Count > 0;
                if (!hadSavedPositions) FirstLayoutPositions(positions, preferences.Categories);

                sizes.Clear();
                DefaultSizes(sizes, preferences.Categories);
                MergeSizes(sizes, AsDict(Get(cfg, "sizes")));

                collapsed = NormalizeStringList(Get(cfg, "collapsed"));
                // 胶囊模式切换立即生效：开启 → 折叠全部分区（用户马上看到所有分区变成图标胶囊）；
                // 关闭 → 展开全部。保证"选择后直接看到效果"。
                if (preferences.CapsuleMode != previousCapsuleMode)
                {
                    collapsed.Clear();
                    if (preferences.CapsuleMode)
                    {
                        foreach (CategoryData category in preferences.Categories) collapsed.Add(category.Id);
                    }
                }
                expandPushback.Clear();              // 配置重载后所有推挤记录失效
                // 新建分类等缺位置的分类分配空闲位；随后统一解决遗留重叠（历史配置/展开叠加）
                AssignMissingPositions();
                // 启动时严格保留用户已经保存的位置。只有首次生成默认布局，或胶囊/缩放变化
                // 可能让旧位置重新发生碰撞时，才在这里做一次避让，避免每次重启都改回手动布局。
                if (!hadSavedPositions || previousCapsuleMode != preferences.CapsuleMode || previousUiScale != preferences.UiScale)
                    ResolvePanelOverlaps(null);
                pinned = NormalizeStringList(Get(cfg, "pinned"));
                viewModes.Clear();
                MergeModeDict(viewModes, AsDict(Get(cfg, "viewModes")));
                sortModes.Clear();
                MergeModeDict(sortModes, AsDict(Get(cfg, "sortModes")));
                itemLayouts.Clear();
                MergeItemLayouts(itemLayouts, AsDict(Get(cfg, "itemLayouts")));

                // loadAdaptivePreset 语义：文件夹对齐非 manual 时 preset 视为 manual
                string preset = AsString(Get(cfg, "adaptivePreset"));
                string alignment = AsString(Get(cfg, "folderAlignment"));
                if (preset != "balanced" && preset != "grid" && preset != "columns" && preset != "corners" && preset != "right-dock") preset = "manual";
                if (alignment != "left" && alignment != "center" && alignment != "right") alignment = "manual";
                if (alignment != "manual") preset = "manual";
                adaptivePreset = preset;
                folderAlignment = alignment;
                reflowShouldExpand = previousAdaptivePreset != adaptivePreset || previousFolderAlignment != folderAlignment;

                organizationHistory = NormalizeHistory(Get(cfg, "organizationHistory"));
                operationHistory.Clear();
                operationHistory.AddRange(NormalizeHistoryBatches(Get(cfg, "operationHistory")));
                redoHistory.Clear();
                redoHistory.AddRange(NormalizeHistoryBatches(Get(cfg, "redoHistory")));
                if (operationHistory.Count == 0 && organizationHistory.Count > 0)
                    operationHistory.Add(new List<HistoryEntry>(organizationHistory));

                if (cfg.ContainsKey("notes"))
                {
                    notes.Clear();
                    notes.AddRange(NormalizeNotes(Get(cfg, "notes")));
                }
                else if (notes.Count == 0)
                {
                    notes.AddRange(NormalizeNotes(null));
                }
                MarkDirty();
            }
            RestartAutoTimer();
            host.WatchPortalFolders(GetPortalPaths());
            ReflowIfAuto(reflowShouldExpand);       // 自适应布局 / 文件夹对齐非 manual 时重排
            RunScan(true);
            RequestNotesSync();
        }

        /// 序列化当前全部配置（设置窗口初始化加载用）
        internal string SerializeConfig()
        {
            lock (stateLock) { return serializer.Serialize(BuildConfig()); }
        }

        internal List<QuickSearchEntry> GetQuickSearchEntries()
        {
            lock (stateLock)
            {
                Dictionary<string, int> recentRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < preferences.RecentItems.Count; i++)
                    if (!recentRank.ContainsKey(preferences.RecentItems[i])) recentRank[preferences.RecentItems[i]] = i;
                List<QuickSearchEntry> result = new List<QuickSearchEntry>();
                foreach (ManagerItem item in scanItems)
                {
                    if (item == null || String.IsNullOrWhiteSpace(item.Path)) continue;
                    CategoryData category = FindCategoryById(item.CategoryId);
                    int recent;
                    QuickSearchEntry entry = new QuickSearchEntry();
                    entry.Name = item.Name;
                    entry.Path = item.Path;
                    entry.Category = category != null ? category.Name : "未分类";
                    entry.Kind = item.Kind;
                    entry.Favorite = preferences.Favorites.Contains(item.Path);
                    entry.Pinned = preferences.PinnedItems.Contains(item.Path);
                    entry.RecentRank = recentRank.TryGetValue(item.Path, out recent) ? recent : 9999;
                    result.Add(entry);
                }
                result.Sort(delegate(QuickSearchEntry left, QuickSearchEntry right)
                {
                    int leftRank = left.Pinned ? 0 : left.Favorite ? 1 : left.RecentRank < 9999 ? 2 : 3;
                    int rightRank = right.Pinned ? 0 : right.Favorite ? 1 : right.RecentRank < 9999 ? 2 : 3;
                    if (leftRank != rightRank) return leftRank.CompareTo(rightRank);
                    if (left.RecentRank != right.RecentRank && leftRank == 2) return left.RecentRank.CompareTo(right.RecentRank);
                    int name = String.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
                    return name != 0 ? name : String.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
                });
                if (result.Count > 400) result.RemoveRange(400, result.Count - 400);
                return result;
            }
        }

        internal List<Dictionary<string, object>> GetOrganizationPreview()
        {
            lock (stateLock)
            {
                List<ManagerItem> candidates = new List<ManagerItem>();
                foreach (ManagerItem item in scanItems)
                {
                    if (item == null || item.Managed) continue;
                    string classifiedCategory = MatchRuleCategory(item.Name, item.Path, item.Extension, item.CategoryId);
                    if (classifiedCategory == UncategorizedId) continue;
                    if (classifiedCategory != item.CategoryId)
                    {
                        candidates.Add(new ManagerItem
                        {
                            Id = item.Id,
                            Name = item.Name,
                            Path = item.Path,
                            Extension = item.Extension,
                            Kind = item.Kind,
                            Size = item.Size,
                            ModifiedAt = item.ModifiedAt,
                            CategoryId = classifiedCategory,
                            Managed = item.Managed,
                            IconUrl = item.IconUrl
                        });
                    }
                    else candidates.Add(item);
                }
                List<OrganizationMove> plan = BuildOrganizationPlan(candidates);
                List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
                foreach (OrganizationMove move in plan)
                {
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    row["name"] = move.Item.Name;
                    row["source"] = move.Source;
                    row["destination"] = move.Destination;
                    CategoryData category = FindCategoryById(move.CategoryId);
                    row["category"] = category != null ? category.Name : "未分类";
                    row["categoryId"] = move.CategoryId;
                    row["ruleMatched"] = MatchRuleCategory(move.Item.Name, move.Item.Path, move.Item.Extension, UncategorizedId) != UncategorizedId;
                    result.Add(row);
                }
                return result;
            }
        }

        internal Dictionary<string, object> GetHistorySummary()
        {
            lock (stateLock)
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                result["undoCount"] = operationHistory.Count;
                result["redoCount"] = redoHistory.Count;
                List<object> batches = new List<object>();
                for (int i = 0; i < operationHistory.Count; i++)
                {
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    row["index"] = i + 1;
                    row["count"] = operationHistory[i].Count;
                    batches.Add(row);
                }
                result["batches"] = batches;
                return result;
            }
        }

        private Dictionary<string, object> BuildConfig()
        {
            Dictionary<string, object> cfg = new Dictionary<string, object>();
            cfg["preferences"] = BuildPreferencesDict();
            cfg["positions"] = PositionDict();
            cfg["sizes"] = SizeDict();
            cfg["collapsed"] = new List<string>(collapsed);
            cfg["pinned"] = new List<string>(pinned);
            cfg["viewModes"] = Dict(viewModes);
            cfg["sortModes"] = Dict(sortModes);
            Dictionary<string, object> layouts = new Dictionary<string, object>();
            foreach (KeyValuePair<string, ItemLayoutData> pair in itemLayouts)
            {
                Dictionary<string, object> layout = new Dictionary<string, object>();
                layout["itemSize"] = pair.Value.ItemSize;
                layout["iconSize"] = pair.Value.IconSize;
                layout["gap"] = pair.Value.Gap;
                layout["alignment"] = pair.Value.Alignment;
                layout["columns"] = pair.Value.Columns;
                layout["showLabels"] = pair.Value.ShowLabels;
                layout["labelSize"] = pair.Value.LabelSize;
                layouts[pair.Key] = layout;
            }
            cfg["itemLayouts"] = layouts;
            cfg["adaptivePreset"] = adaptivePreset;
            cfg["folderAlignment"] = folderAlignment;
            List<object> history = new List<object>();
            foreach (HistoryEntry entry in organizationHistory)
            {
                history.Add(BuildHistoryDict(entry));
            }
            cfg["organizationHistory"] = history;
            cfg["operationHistory"] = BuildHistoryBatches(operationHistory);
            cfg["redoHistory"] = BuildHistoryBatches(redoHistory);

            List<object> notesList = new List<object>();
            foreach (NoteData note in notes)
            {
                Dictionary<string, object> n = new Dictionary<string, object>();
                n["id"] = note.Id;
                n["title"] = note.Title;
                n["mode"] = note.Mode;
                n["color"] = note.Color;
                n["x"] = note.X;
                n["y"] = note.Y;
                n["width"] = note.Width;
                n["height"] = note.Height;
                n["pinned"] = note.Pinned;
                n["collapsed"] = note.Collapsed;
                n["noteContent"] = note.NoteContent != null ? note.NoteContent : "";
                List<object> todos = new List<object>();
                if (note.Todos != null)
                {
                    foreach (TodoItemData t in note.Todos)
                    {
                        Dictionary<string, object> td = new Dictionary<string, object>();
                        td["id"] = t.Id;
                        td["text"] = t.Text;
                        td["done"] = t.Done;
                        td["createdAt"] = t.CreatedAt;
                        todos.Add(td);
                    }
                }
                n["todos"] = todos;
                notesList.Add(n);
            }
            cfg["notes"] = notesList;

            return cfg;
        }

        private static Dictionary<string, object> BuildHistoryDict(HistoryEntry entry)
        {
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["source"] = entry.Source;
            item["destination"] = entry.Destination;
            item["name"] = entry.Name;
            return item;
        }

        private static List<object> BuildHistoryBatches(List<List<HistoryEntry>> batches)
        {
            List<object> result = new List<object>();
            int start = Math.Max(0, batches.Count - MaxOperationHistory);
            for (int i = start; i < batches.Count; i++)
            {
                List<object> batch = new List<object>();
                foreach (HistoryEntry entry in batches[i]) batch.Add(BuildHistoryDict(entry));
                result.Add(batch);
            }
            return result;
        }

        private Dictionary<string, object> BuildPreferencesDict()
        {
            Dictionary<string, object> prefs = new Dictionary<string, object>();
            List<object> categories = new List<object>();
            foreach (CategoryData category in preferences.Categories)
            {
                Dictionary<string, object> c = new Dictionary<string, object>();
                c["id"] = category.Id;
                c["name"] = category.Name;
                c["icon"] = category.Icon;
                c["color"] = category.Color;
                c["extensions"] = new List<string>(category.Extensions);
                c["acceptsFolders"] = category.AcceptsFolders;
                if (!String.IsNullOrWhiteSpace(category.PortalPath)) c["portalPath"] = category.PortalPath;
                categories.Add(c);
            }
            prefs["categories"] = categories;
            prefs["automaticScan"] = preferences.AutomaticScan;
            prefs["automaticOrganize"] = preferences.AutomaticOrganize;
            prefs["organizeDelaySeconds"] = preferences.OrganizeDelaySeconds;
            prefs["organizeMode"] = preferences.OrganizeMode;
            prefs["magicColor"] = preferences.MagicColor;
            Dictionary<string, object> pins = new Dictionary<string, object>();
            foreach (KeyValuePair<string, List<string>> pair in preferences.ReferencePins)
                pins[pair.Key] = new List<string>(pair.Value);
            prefs["referencePins"] = pins;
            prefs["showHiddenFiles"] = preferences.ShowHiddenFiles;
            prefs["compactView"] = preferences.CompactView;
            prefs["capsuleMode"] = preferences.CapsuleMode;
            prefs["noteCapsuleMode"] = preferences.NoteCapsuleMode;
            prefs["desktopContextMenu"] = preferences.DesktopContextMenu;
            prefs["glassOpacity"] = preferences.GlassOpacity;
            prefs["material"] = preferences.Material;   // 回传给前端：面板真亚克力开关（前端可后续暴露为设置项）
            prefs["theme"] = preferences.Theme;
            prefs["colorScheme"] = preferences.ColorScheme;
            prefs["customColor"] = preferences.CustomColor;
            prefs["customSurfaceColor"] = preferences.CustomSurfaceColor;
            prefs["uiScale"] = preferences.UiScale;
            prefs["showExtensions"] = preferences.ShowExtensions;
            prefs["labelPosition"] = preferences.LabelPosition;
            prefs["autoHide"] = preferences.AutoHide;
            prefs["autoHideDelaySeconds"] = preferences.AutoHideDelaySeconds;
            List<object> rules = new List<object>();
            foreach (OrganizationRuleData rule in preferences.Rules)
            {
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["id"] = rule.Id;
                item["name"] = rule.Name;
                item["enabled"] = rule.Enabled;
                item["match"] = rule.Match;
                item["pattern"] = rule.Pattern;
                item["categoryId"] = rule.CategoryId;
                item["priority"] = rule.Priority;
                rules.Add(item);
            }
            prefs["rules"] = rules;
            prefs["favorites"] = new List<string>(preferences.Favorites);
            prefs["recentItems"] = new List<string>(preferences.RecentItems);
            prefs["pinnedItems"] = new List<string>(preferences.PinnedItems);
            prefs["activeWorkspaceId"] = preferences.ActiveWorkspaceId;
            List<object> workspaces = new List<object>();
            foreach (WorkspaceData workspace in preferences.Workspaces) workspaces.Add(BuildWorkspaceDict(workspace));
            prefs["workspaces"] = workspaces;
            return prefs;
        }

        private static Dictionary<string, object> BuildWorkspaceDict(WorkspaceData workspace)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["id"] = workspace.Id;
            result["name"] = workspace.Name;
            result["positions"] = PositionDict(workspace.Positions);
            result["sizes"] = SizeDict(workspace.Sizes);
            result["collapsed"] = new List<string>(workspace.Collapsed);
            result["pinned"] = new List<string>(workspace.Pinned);
            result["viewModes"] = Dict(workspace.ViewModes);
            result["sortModes"] = Dict(workspace.SortModes);
            Dictionary<string, object> layouts = new Dictionary<string, object>();
            foreach (KeyValuePair<string, ItemLayoutData> pair in workspace.ItemLayouts)
            {
                Dictionary<string, object> layout = new Dictionary<string, object>();
                layout["itemSize"] = pair.Value.ItemSize;
                layout["iconSize"] = pair.Value.IconSize;
                layout["gap"] = pair.Value.Gap;
                layout["alignment"] = pair.Value.Alignment;
                layout["columns"] = pair.Value.Columns;
                layout["showLabels"] = pair.Value.ShowLabels;
                layout["labelSize"] = pair.Value.LabelSize;
                layouts[pair.Key] = layout;
            }
            result["itemLayouts"] = layouts;
            result["adaptivePreset"] = workspace.AdaptivePreset;
            result["folderAlignment"] = workspace.FolderAlignment;
            return result;
        }

        private Dictionary<string, object> PositionDict()
        {
            return PositionDict(positions);
        }

        private static Dictionary<string, object> PositionDict(Dictionary<string, PositionData> source)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, PositionData> pair in source)
            {
                Dictionary<string, object> p = new Dictionary<string, object>();
                p["x"] = pair.Value.X;
                p["y"] = pair.Value.Y;
                result[pair.Key] = p;
            }
            return result;
        }

        private Dictionary<string, object> SizeDict()
        {
            return SizeDict(sizes);
        }

        private static Dictionary<string, object> SizeDict(Dictionary<string, SizeData> source)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, SizeData> pair in source)
            {
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["width"] = pair.Value.Width;
                s["height"] = pair.Value.Height;
                result[pair.Key] = s;
            }
            return result;
        }

        private static Dictionary<string, object> Dict(Dictionary<string, string> source)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, string> pair in source) result[pair.Key] = pair.Value;
            return result;
        }

        /// 变更后 180ms 防抖写盘
        private void MarkDirty()
        {
            if (disposed) return;
            lock (stateLock)
            {
                if (saveTimer == null) saveTimer = new Timer(OnSaveTimer, null, ConfigSaveDebounceMs, Timeout.Infinite);
                else saveTimer.Change(ConfigSaveDebounceMs, Timeout.Infinite);
            }
        }

        private void OnSaveTimer(object state)
        {
            lock (stateLock) { DisposeTimer(ref saveTimer); }
            SaveConfigNow();
        }

        /// 立即写盘；返回是否成功（组织 toast 需要该结果）。
        /// 原子写入：先写临时文件再替换，替换时把上一份好配置留作 config.json.bak 快照，
        /// 写入中途断电/崩溃不会损坏主配置，下次启动可从快照恢复。
        private bool SaveConfigNow()
        {
            lock (stateLock)
            {
                if (disposed) return false;
                string path = GetConfigPath();
                string temp = path + ".tmp";
                string backup = GetConfigBackupPath();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(temp, serializer.Serialize(BuildConfig()), new UTF8Encoding(false));
                    if (File.Exists(path))
                    {
                        // File.Replace 原子替换并把旧文件转存为 .bak；旧 .bak 损坏/被占用时退化为直接覆盖
                        try { File.Replace(temp, path, backup); }
                        catch (IOException) { File.Copy(temp, path, true); TryDeleteFile(temp); }
                        catch (UnauthorizedAccessException) { File.Copy(temp, path, true); TryDeleteFile(temp); }
                    }
                    else
                    {
                        File.Move(temp, path);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    host.Log("配置保存失败：" + ex.Message);
                    TryDeleteFile(temp);
                    return false;
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        // ---- 归一化（对齐 preferences.ts / categoryValidation.ts / layout.ts） ----
        internal static PreferencesData NormalizePreferences(Dictionary<string, object> value)
        {
            PreferencesData result = new PreferencesData();
            // categories：最多 100 个，逐个 normalize + id/name 去重
            List<CategoryData> categories = new List<CategoryData>();
            HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object rawCategories;
            if (value.TryGetValue("categories", out rawCategories) && rawCategories is object[])
            {
                object[] arr = (object[])rawCategories;
                int limit = Math.Min(arr.Length, MaxCategoryCount);
                for (int i = 0; i < limit; i++)
                {
                    CategoryData category = NormalizeCategory(arr[i], "recovered-" + i);
                    if (category == null) continue;
                    if (seenIds.Contains(category.Id) || seenNames.Contains(category.Name)) continue;
                    seenIds.Add(category.Id);
                    seenNames.Add(category.Name);
                    categories.Add(category);
                }
            }
            if (categories.Count == 0) categories = DefaultCategories();
            result.Categories = categories;
            result.AutomaticScan = AsBool(Get(value, "automaticScan"));
            result.AutomaticOrganize = AsBool(Get(value, "automaticOrganize"));
            result.OrganizeDelaySeconds = (int)Math.Floor(BoundedNumber(Get(value, "organizeDelaySeconds"), 10, 5, 60));
            result.OrganizeMode = AsString(Get(value, "organizeMode")) == "reference" ? "reference" : "move";
            result.MagicColor = AsBool(Get(value, "magicColor"));
            result.ReferencePins = NormalizeReferencePins(Get(value, "referencePins"));
            result.ShowHiddenFiles = AsBool(Get(value, "showHiddenFiles"));
            result.CompactView = AsBool(Get(value, "compactView"));
            result.CapsuleMode = AsBool(Get(value, "capsuleMode"));
            result.NoteCapsuleMode = AsBool(Get(value, "noteCapsuleMode"));
            result.DesktopContextMenu = Get(value, "desktopContextMenu") != null ? AsBool(Get(value, "desktopContextMenu")) : true;
            result.GlassOpacity = (int)Math.Floor(BoundedNumber(Get(value, "glassOpacity"), DefaultGlassOpacity, 0, 100));
            // 前端可选发送 material（acrylic|solid）；不发送时保持默认 acrylic
            string material = AsString(Get(value, "material"));
            result.Material = material == "solid" ? "solid" : "acrylic";
            string theme = AsString(Get(value, "theme"));
            result.Theme = (theme == "dark" || theme == "system") ? theme : "light";
            string scheme = AsString(Get(value, "colorScheme"));
            result.ColorScheme = (scheme == "white" || scheme == "warm" || scheme == "ink" || scheme == "forest" || scheme == "rose" || scheme == "custom") ? scheme : "white";
            string customColor = AsString(Get(value, "customColor"));
            result.CustomColor = HexColorPattern.IsMatch(customColor) ? customColor : DefaultCustomColor;
            string customSurfaceColor = AsString(Get(value, "customSurfaceColor"));
            result.CustomSurfaceColor = HexColorPattern.IsMatch(customSurfaceColor) ? customSurfaceColor : DefaultCustomSurfaceColor;
            result.UiScale = (int)Math.Floor(BoundedNumber(Get(value, "uiScale"), 100, 80, 130));
            result.ShowExtensions = AsBool(Get(value, "showExtensions"));
            string labelPosition = AsString(Get(value, "labelPosition"));
            result.LabelPosition = labelPosition == "right" ? "right" : "bottom";
            result.AutoHide = AsBool(Get(value, "autoHide"));
            result.AutoHideDelaySeconds = (int)Math.Floor(BoundedNumber(Get(value, "autoHideDelaySeconds"), 2, 1, 10));
            result.Rules = NormalizeRules(Get(value, "rules"), result.Categories);
            result.Favorites = NormalizeStringList(Get(value, "favorites"), 120);
            result.RecentItems = NormalizeStringList(Get(value, "recentItems"), 30);
            result.PinnedItems = NormalizeStringList(Get(value, "pinnedItems"), 120);
            result.Workspaces = NormalizeWorkspaces(Get(value, "workspaces"));
            string activeWorkspaceId = AsString(Get(value, "activeWorkspaceId"));
            result.ActiveWorkspaceId = activeWorkspaceId;
            return result;
        }

        /// 对齐 defaults.ts 的 8 个默认分类
        internal static List<CategoryData> DefaultCategories()
        {
            return new List<CategoryData>
            {
                MakeCategory("shortcuts", "快捷方式", "chiikawa-wave", "#3478f6", new string[] { "lnk", "url", "appref-ms" }, false),
                MakeCategory("documents", "工作文档", "chiikawa-read", "#a9cbe4", new string[] { "doc", "docx", "pdf", "txt", "rtf", "odt", "md", "pages" }, false),
                MakeCategory("sheets", "表格数据", "chiikawa-organize", "#9ecfc0", new string[] { "xls", "xlsx", "csv", "numbers", "ods" }, false),
                MakeCategory("slides", "演示文稿", "chiikawa-search", "#f4c968", new string[] { "ppt", "pptx", "key", "odp" }, false),
                MakeCategory("images", "图片素材", "chiikawa-idle", "#c5b7df", new string[] { "jpg", "jpeg", "png", "gif", "webp", "svg", "heic", "bmp" }, false),
                MakeCategory("media", "影音文件", "chiikawa-wave", "#5856d6", new string[] { "mp3", "wav", "m4a", "flac", "mp4", "mov", "mkv", "avi" }, false),
                MakeCategory("archives", "压缩文件", "chiikawa-carry-folder", "#dfb07b", new string[] { "zip", "rar", "7z", "tar", "gz" }, false),
                MakeCategory("folders", "文件夹", "chiikawa-organize", "#9ecfc0", new string[] { }, true),
            };
        }

        private static CategoryData MakeCategory(string id, string name, string icon, string color, string[] extensions, bool acceptsFolders)
        {
            CategoryData category = new CategoryData();
            category.Id = id;
            category.Name = name;
            category.Icon = icon;
            category.Color = color;
            category.Extensions = new List<string>(extensions);
            category.AcceptsFolders = acceptsFolders;
            return category;
        }

        private static string MigrateDefaultIcon(string id, string icon)
        {
            if (id == "shortcuts" && (icon == "rocket" || icon == "shortcut")) return "chiikawa-wave";
            if (id == "documents" && icon == "file-text") return "chiikawa-read";
            if (id == "sheets" && icon == "sheet") return "chiikawa-organize";
            if (id == "slides" && icon == "presentation") return "chiikawa-search";
            if (id == "images" && icon == "image") return "chiikawa-idle";
            if (id == "media" && icon == "play") return "chiikawa-wave";
            if (id == "archives" && icon == "archive") return "chiikawa-carry-folder";
            if (id == "folders" && icon == "folder") return "chiikawa-organize";
            return icon;
        }

        private static string MigrateDefaultColor(string id, string color)
        {
            if (id == "shortcuts" && (String.Equals(color, "#ff6b5f", StringComparison.OrdinalIgnoreCase) || String.Equals(color, "#e98687", StringComparison.OrdinalIgnoreCase))) return "#3478f6";
            if (id == "documents" && String.Equals(color, "#3478f6", StringComparison.OrdinalIgnoreCase)) return "#a9cbe4";
            if (id == "sheets" && String.Equals(color, "#34a853", StringComparison.OrdinalIgnoreCase)) return "#9ecfc0";
            if (id == "slides" && String.Equals(color, "#ff9f0a", StringComparison.OrdinalIgnoreCase)) return "#f4c968";
            if (id == "images" && String.Equals(color, "#af52de", StringComparison.OrdinalIgnoreCase)) return "#c5b7df";
            if (id == "media" && (String.Equals(color, "#ff375f", StringComparison.OrdinalIgnoreCase) || String.Equals(color, "#e4a5b5", StringComparison.OrdinalIgnoreCase))) return "#5856d6";
            if (id == "archives" && String.Equals(color, "#8e8e93", StringComparison.OrdinalIgnoreCase)) return "#dfb07b";
            if (id == "folders" && String.Equals(color, "#18a999", StringComparison.OrdinalIgnoreCase)) return "#9ecfc0";
            return color;
        }

        /// 对齐 categoryValidation.ts 的 normalizeCategory
        internal static CategoryData NormalizeCategory(object value, string fallbackId)
        {
            Dictionary<string, object> candidate = value as Dictionary<string, object>;
            if (candidate == null) return null;
            try
            {
                string name = NormalizeCategoryName(AsString(Get(candidate, "name")), null);
                List<string> extensions = new List<string>();
                HashSet<string> seenExtensions = new HashSet<string>(StringComparer.Ordinal);
                object rawExtensions;
                if (candidate.TryGetValue("extensions", out rawExtensions) && rawExtensions is object[])
                {
                    foreach (object ext in (object[])rawExtensions)
                    {
                        if (ext == null) continue;
                        string normalized = Convert.ToString(ext).Trim().ToLowerInvariant();
                        if (normalized.StartsWith(".")) normalized = normalized.Substring(1);
                        if (normalized.Length == 0 || seenExtensions.Contains(normalized)) continue;
                        seenExtensions.Add(normalized);
                        extensions.Add(normalized);
                    }
                }
                CategoryData category = new CategoryData();
                string id = AsString(Get(candidate, "id"));
                category.Id = !String.IsNullOrWhiteSpace(id) ? id : fallbackId;
                category.Name = name;
                string icon = AsString(Get(candidate, "icon"));
                category.Icon = MigrateDefaultIcon(category.Id, !String.IsNullOrWhiteSpace(icon) ? icon : FallbackCategoryIcon);
                string color = AsString(Get(candidate, "color"));
                category.Color = MigrateDefaultColor(category.Id, HexColorPattern.IsMatch(color) ? color : FallbackCategoryColor);
                category.Extensions = extensions;
                category.AcceptsFolders = AsTruthy(Get(candidate, "acceptsFolders"));
                // portalPath 非 string 时丢弃（不保留非法值）
                string portalPath = AsString(Get(candidate, "portalPath"));
                if (!String.IsNullOrWhiteSpace(portalPath)) category.PortalPath = portalPath;
                return category;
            }
            catch
            {
                return null;
            }
        }

        /// 对齐 categoryValidation.ts 的 normalizeCategoryName
        internal static string NormalizeCategoryName(string value, List<string> existingNames)
        {
            string name = value == null ? "" : value.Trim();
            if (name.Length == 0) throw new InvalidOperationException("请输入分类名称");
            if (name.Length > 20) throw new InvalidOperationException("分类名称不能超过 20 个字符");
            if (name == "." || name == "..") throw new InvalidOperationException("分类名称不能是“.”或“..”");
            if (InvalidWindowsNameCharacters.IsMatch(name)) throw new InvalidOperationException("分类名称不能包含 \\ / : * ? \" < > | 等字符");
            if (name.EndsWith(".")) throw new InvalidOperationException("分类名称不能以句点结尾");
            if (ReservedWindowsNames.IsMatch(name)) throw new InvalidOperationException("“" + name + "”是 Windows 保留名称");
            if (existingNames != null)
            {
                string lowered = name.ToLowerInvariant();
                foreach (string candidate in existingNames)
                {
                    if (candidate != null && candidate.Trim().ToLowerInvariant() == lowered)
                        throw new InvalidOperationException("已经存在“" + name + "”分区");
                }
            }
            return name;
        }

        internal static Dictionary<string, List<string>> NormalizeReferencePins(object value)
        {
            Dictionary<string, List<string>> pins = new Dictionary<string, List<string>>();
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict == null) return pins;
            foreach (KeyValuePair<string, object> pair in dict)
            {
                object[] arr = pair.Value as object[];
                if (arr == null) continue;
                List<string> list = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (object path in arr)
                {
                    // 对齐 JS filter(typeof === 'string')：非字符串条目忽略
                    if (!(path is string)) continue;
                    string item = (string)path;
                    if (seen.Contains(item)) continue;
                    seen.Add(item);
                    list.Add(item);
                }
                pins[pair.Key] = list;
            }
            return pins;
        }

        private static List<OrganizationRuleData> NormalizeRules(object value, List<CategoryData> categories)
        {
            List<OrganizationRuleData> result = new List<OrganizationRuleData>();
            object[] arr = value as object[];
            if (arr == null) return result;
            HashSet<string> categoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CategoryData category in categories) categoryIds.Add(category.Id);
            for (int i = 0; i < Math.Min(100, arr.Length); i++)
            {
                Dictionary<string, object> dict = arr[i] as Dictionary<string, object>;
                if (dict == null) continue;
                string pattern = AsString(Get(dict, "pattern")).Trim();
                string categoryId = AsString(Get(dict, "categoryId"));
                if (pattern.Length == 0 || !categoryIds.Contains(categoryId)) continue;
                OrganizationRuleData rule = new OrganizationRuleData();
                rule.Id = AsString(Get(dict, "id"));
                if (rule.Id.Length == 0) rule.Id = "rule-" + (i + 1).ToString(CultureInfo.InvariantCulture);
                rule.Name = AsString(Get(dict, "name")).Trim();
                if (rule.Name.Length == 0) rule.Name = "规则 " + (i + 1).ToString(CultureInfo.InvariantCulture);
                rule.Enabled = !dict.ContainsKey("enabled") || AsBool(Get(dict, "enabled"));
                string match = AsString(Get(dict, "match"));
                rule.Match = match == "name" || match == "path" ? match : "extension";
                rule.Pattern = pattern;
                rule.CategoryId = categoryId;
                rule.Priority = (int)Math.Floor(BoundedNumber(Get(dict, "priority"), i + 1, 1, 999));
                result.Add(rule);
            }
            result.Sort(delegate(OrganizationRuleData left, OrganizationRuleData right) { return left.Priority.CompareTo(right.Priority); });
            return result;
        }

        private static List<WorkspaceData> NormalizeWorkspaces(object value)
        {
            List<WorkspaceData> result = new List<WorkspaceData>();
            object[] arr = value as object[];
            if (arr == null) return result;
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Math.Min(12, arr.Length); i++)
            {
                Dictionary<string, object> dict = arr[i] as Dictionary<string, object>;
                if (dict == null) continue;
                string name = AsString(Get(dict, "name")).Trim();
                if (name.Length == 0) continue;
                WorkspaceData workspace = new WorkspaceData();
                workspace.Id = AsString(Get(dict, "id"));
                if (workspace.Id.Length == 0) workspace.Id = "workspace-" + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (!ids.Add(workspace.Id)) continue;
                workspace.Name = name;
                MergePositions(workspace.Positions, AsDict(Get(dict, "positions")));
                MergeSizes(workspace.Sizes, AsDict(Get(dict, "sizes")));
                workspace.Collapsed = NormalizeStringList(Get(dict, "collapsed"), 120);
                workspace.Pinned = NormalizeStringList(Get(dict, "pinned"), 120);
                MergeModeDict(workspace.ViewModes, AsDict(Get(dict, "viewModes")));
                MergeModeDict(workspace.SortModes, AsDict(Get(dict, "sortModes")));
                MergeItemLayouts(workspace.ItemLayouts, AsDict(Get(dict, "itemLayouts")));
                string preset = AsString(Get(dict, "adaptivePreset"));
                workspace.AdaptivePreset = preset == "balanced" || preset == "grid" || preset == "columns" || preset == "corners" || preset == "right-dock" ? preset : "manual";
                string alignment = AsString(Get(dict, "folderAlignment"));
                workspace.FolderAlignment = alignment == "left" || alignment == "center" || alignment == "right" ? alignment : "manual";
                result.Add(workspace);
            }
            return result;
        }

        private static List<List<HistoryEntry>> NormalizeHistoryBatches(object value)
        {
            List<List<HistoryEntry>> result = new List<List<HistoryEntry>>();
            object[] batches = value as object[];
            if (batches == null) return result;
            int start = Math.Max(0, batches.Length - MaxOperationHistory);
            for (int i = start; i < batches.Length; i++)
            {
                List<HistoryEntry> batch = NormalizeHistory(batches[i]);
                if (batch.Count > 0) result.Add(batch);
            }
            return result;
        }

        private static List<string> NormalizeStringList(object value, int maxCount)
        {
            List<string> result = new List<string>();
            object[] arr = value as object[];
            if (arr == null) return result;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object entry in arr)
            {
                if (!(entry is string)) continue;
                string item = (string)entry;
                if (seen.Contains(item)) continue;
                seen.Add(item);
                result.Add(item);
                if (result.Count >= maxCount) break;
            }
            return result;
        }

        private static List<string> NormalizeStringList(object value)
        {
            return NormalizeStringList(value, 120);
        }

        private static List<HistoryEntry> NormalizeHistory(object value)
        {
            List<HistoryEntry> result = new List<HistoryEntry>();
            object[] arr = value as object[];
            if (arr == null) return result;
            foreach (object entry in arr)
            {
                Dictionary<string, object> dict = entry as Dictionary<string, object>;
                if (dict == null) continue;
                string source = AsString(Get(dict, "source"));
                string destination = AsString(Get(dict, "destination"));
                string name = AsString(Get(dict, "name"));
                if (source == null || destination == null || name == null) continue;
                HistoryEntry item = new HistoryEntry();
                item.Source = source;
                item.Destination = destination;
                item.Name = name;
                result.Add(item);
            }
            return result;
        }

        private static void MergePositions(Dictionary<string, PositionData> target, Dictionary<string, object> value)
        {
            if (value == null) return;
            foreach (KeyValuePair<string, object> pair in value)
            {
                Dictionary<string, object> pos = pair.Value as Dictionary<string, object>;
                if (pos == null) continue;
                double x = AsNumber(Get(pos, "x"));
                double y = AsNumber(Get(pos, "y"));
                if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) continue;
                PositionData data = new PositionData();
                data.X = x;
                data.Y = y;
                target[pair.Key] = data;
            }
        }

        private static void DefaultSizes(Dictionary<string, SizeData> target, List<CategoryData> categories)
        {
            foreach (CategoryData category in categories)
            {
                SizeData data = new SizeData();
                data.Width = 292;
                data.Height = 238;
                target[category.Id] = data;
            }
        }

        private static void MergeSizes(Dictionary<string, SizeData> target, Dictionary<string, object> value)
        {
            if (value == null) return;
            foreach (KeyValuePair<string, object> pair in value)
            {
                Dictionary<string, object> size = pair.Value as Dictionary<string, object>;
                if (size == null) continue;
                double width = AsNumber(Get(size, "width"));
                double height = AsNumber(Get(size, "height"));
                if (double.IsNaN(width) || double.IsInfinity(width) || double.IsNaN(height) || double.IsInfinity(height)) continue;
                SizeData data;
                if (!target.TryGetValue(pair.Key, out data)) data = new SizeData();
                data.Width = width;
                data.Height = height;
                target[pair.Key] = data;
            }
        }

        /// loadPositions 语义：无保存布局时按工作区宽度生成网格
        private void FirstLayoutPositions(Dictionary<string, PositionData> target, List<CategoryData> categories)
        {
            double width = host.WorkAreaWidth;
            int columns = width < 900 ? 2 : Math.Min(4, Math.Max(2, (int)Math.Floor((width - 80) / 320)));
            for (int index = 0; index < categories.Count; index++)
            {
                PositionData data = new PositionData();
                data.X = 24 + (index % columns) * 292;
                data.Y = 24 + Math.Floor((double)index / columns) * 238;
                target[categories[index].Id] = data;
            }
        }

        // 堆叠去重：历史数据/旧预设可能让多个分区落在同一坐标（完全重叠）。
        // 按分类顺序，遇到与已放置分区同坐标的分区，挪到下一个空闲网格位。
        private static void DedupeStackedPositions(Dictionary<string, PositionData> target, List<CategoryData> categories)
        {
            HashSet<string> taken = new HashSet<string>();
            foreach (CategoryData category in categories)
            {
                PositionData pos;
                if (!target.TryGetValue(category.Id, out pos)) continue;
                string key = (int)Math.Round(pos.X) + "," + (int)Math.Round(pos.Y);
                if (taken.Add(key)) continue;   // 首次出现：占用该坐标
                // 已占用：挪到下一个空闲网格位（24 + 列*292, 24 + 行*238）
                bool placed = false;
                for (int row = 0; row < 8 && !placed; row++)
                {
                    for (int col = 0; col < 6; col++)
                    {
                        string candidate = (24 + col * 292) + "," + (24 + row * 238);
                        if (taken.Contains(candidate)) continue;
                        pos.X = 24 + col * 292;
                        pos.Y = 24 + row * 238;
                        taken.Add(candidate);
                        placed = true;
                        break;
                    }
                }
            }
        }

        // 展开避让常量
        private const double OverlapGap = 6;        // 避让后的面板间距
        private const double OverlapEpsilon = 0.5;  // 亚像素重叠容差

        /// 面板占地：折叠时按胶囊/标题条形态（与 PanelWindow 常量一致），展开时按配置尺寸。
        /// WPF 的最小尺寸也要计入避让，否则管理器认为“有空位”，窗口实际却会互相压住。
        private void PanelFootprint(string categoryId, out double width, out double height)
        {
            SizeData size;
            if (!sizes.TryGetValue(categoryId, out size)) { width = 292; height = 238; }
            else { width = size.Width; height = size.Height; }
            width = Math.Max(PanelMinWidth, width);
            height = Math.Max(PanelMinHeight, height);
            if (collapsed.Contains(categoryId))
            {
                width = preferences.CapsuleMode ? PanelWindow.CapsuleSize : width;
                height = preferences.CapsuleMode ? PanelWindow.CapsuleSize : PanelWindow.HeaderBarHeight;
            }
        }

        private static bool PanelOverlapsAny(PanelRect rect, List<PanelRect> others)
        {
            for (int i = 0; i < others.Count; i++)
            {
                PanelRect other = others[i];
                bool overlaps = rect.X < other.X + other.Width - OverlapEpsilon
                    && other.X < rect.X + rect.Width - OverlapEpsilon
                    && rect.Y < other.Y + other.Height - OverlapEpsilon
                    && other.Y < rect.Y + rect.Height - OverlapEpsilon;
                if (overlaps) return true;
            }
            return false;
        }

        private static void ConsiderFreeSpot(PanelRect candidate, PanelRect origin, List<PanelRect> occupied, ref PanelRect? best, ref double bestDistance)
        {
            if (PanelOverlapsAny(candidate, occupied)) return;
            double dx = candidate.X - origin.X;
            double dy = candidate.Y - origin.Y;
            double distance = dx * dx + dy * dy;
            double bias = 1.0;
            if (candidate.X < origin.X - 0.5) bias *= 1.18;   // 向左让位略降优先级
            if (candidate.Y < origin.Y - 0.5) bias *= 1.18;   // 向上让位略降优先级
            double score = distance * bias;
            if (score < bestDistance)
            {
                bestDistance = score;
                best = candidate;
            }
        }

        /// 在 occupied 之外为 rect 找最近的空闲位置。
        /// 先检查边界/邻接候选，若候选集合没有命中，再用小步长扫描整个工作区，
        /// 避免多个展开面板把可用位置切成不规则空隙时错误地判定“无空位”。
        private static PanelRect? FindFreeSpot(PanelRect rect, List<PanelRect> occupied, double workWidth, double workHeight)
        {
            if (rect.Width > workWidth + OverlapEpsilon || rect.Height > workHeight + OverlapEpsilon) return null;
            List<double> xs = new List<double> { rect.X, 0, workWidth - rect.Width };
            List<double> ys = new List<double> { rect.Y, 0, workHeight - rect.Height };
            for (int i = 0; i < occupied.Count; i++)
            {
                PanelRect other = occupied[i];
                xs.Add(other.X - OverlapGap - rect.Width);
                xs.Add(other.X + other.Width + OverlapGap);
                ys.Add(other.Y - OverlapGap - rect.Height);
                ys.Add(other.Y + other.Height + OverlapGap);
            }
            PanelRect? best = null;
            double bestDistance = double.MaxValue;
            for (int xi = 0; xi < xs.Count; xi++)
            {
                double x = xs[xi];
                if (x < 0 || x + rect.Width > workWidth) continue;
                for (int yi = 0; yi < ys.Count; yi++)
                {
                    double y = ys[yi];
                    if (y < 0 || y + rect.Height > workHeight) continue;
                    PanelRect candidate = new PanelRect(x, y, rect.Width, rect.Height);
                    ConsiderFreeSpot(candidate, rect, occupied, ref best, ref bestDistance);
                }
            }

            if (!best.HasValue)
            {
                double stepX = Math.Max(12, Math.Min(32, rect.Width / 3));
                double stepY = Math.Max(12, Math.Min(32, rect.Height / 3));
                double maxX = Math.Max(0, workWidth - rect.Width);
                double maxY = Math.Max(0, workHeight - rect.Height);
                for (double y = 0; y <= maxY + 0.1; y += stepY)
                {
                    double candidateY = Math.Min(y, maxY);
                    for (double x = 0; x <= maxX + 0.1; x += stepX)
                    {
                        double candidateX = Math.Min(x, maxX);
                        ConsiderFreeSpot(new PanelRect(candidateX, candidateY, rect.Width, rect.Height), rect, occupied, ref best, ref bestDistance);
                    }
                }
            }
            return best;
        }

        /// 展开避让：尽量保证所有面板互不重叠。fixedId 面板保持原位（其展开占地优先占用），
        /// 其余与已占用空间冲突的面板就近让位；fixedId 为 null 时按分类顺序首面板为锚。
        /// 多轮迭代直到一轮无移动（防级联连锁），上限 8 轮保证收敛。
        private void ResolvePanelOverlaps(string fixedId)
        {
            if (disposed) return;
            double workWidth = host.WorkAreaWidth;
            double workHeight = host.WorkAreaHeight;
            const int maxPasses = 8;
            for (int pass = 0; pass < maxPasses; pass++)
            {
                List<PanelRect> occupied = new List<PanelRect>();
                if (fixedId != null)
                {
                    PositionData fixedPos;
                    SizeData fixedSize;
                    if (!positions.TryGetValue(fixedId, out fixedPos) || !sizes.TryGetValue(fixedId, out fixedSize)) return;
                    occupied.Add(new PanelRect(fixedPos.X, fixedPos.Y,
                        Math.Max(PanelMinWidth, Math.Min(workWidth, fixedSize.Width)),
                        Math.Max(PanelMinHeight, Math.Min(workHeight, fixedSize.Height))));
                }
                bool moved = false;
                foreach (CategoryData category in preferences.Categories)
                {
                    string id = category.Id;
                    if (fixedId != null && String.Equals(id, fixedId, StringComparison.Ordinal)) continue;
                    PositionData pos;
                    if (!positions.TryGetValue(id, out pos)) continue;
                    double width, height;
                    PanelFootprint(category.Id, out width, out height);
                    width = Math.Min(workWidth, width);
                    height = Math.Min(workHeight, height);
                    PanelRect rect = new PanelRect(pos.X, pos.Y, width, height);
                    // 折叠态（小图标/胶囊条）保持用户摆放的原位，不被其它展开面板随意推挤移位
                    if (collapsed.Contains(id))
                    {
                        occupied.Add(rect);
                        continue;
                    }
                    if (!PanelOverlapsAny(rect, occupied))
                    {
                        occupied.Add(rect);
                        continue;
                    }
                    PanelRect? free = FindFreeSpot(rect, occupied, workWidth, workHeight);
                    if (free.HasValue)
                    {
                        if (fixedId != null) RecordPushback(fixedId, id, rect.X, rect.Y, free.Value.X, free.Value.Y);
                        positions[id] = new PositionData { X = free.Value.X, Y = free.Value.Y };
                        occupied.Add(free.Value);
                        moved = true;
                    }
                    else
                    {
                        // 展开面板数量超过工作区容量时，自动把低优先级面板退回胶囊/标题条，
                        // 给当前展开内容留出完整的矩形空间；只有连最小形态都放不下时才保留原位。
                        if (!collapsed.Contains(id))
                        {
                            collapsed.Add(id);
                            moved = true;
                            double compactWidth, compactHeight;
                            PanelFootprint(id, out compactWidth, out compactHeight);
                            PanelRect compactRect = new PanelRect(rect.X, rect.Y, compactWidth, compactHeight);
                            PanelRect? compactSpot = FindFreeSpot(compactRect, occupied, workWidth, workHeight);
                            if (compactSpot.HasValue)
                            {
                                if (fixedId != null) RecordPushback(fixedId, id, rect.X, rect.Y, compactSpot.Value.X, compactSpot.Value.Y);
                                positions[id] = new PositionData { X = compactSpot.Value.X, Y = compactSpot.Value.Y };
                                occupied.Add(compactSpot.Value);
                                continue;
                            }
                        }
                        // 屏幕连最小胶囊形态都无法容纳时没有几何上的无重叠解，
                        // 保留当前位置等待工作区变大，避免把窗口推到不可恢复的坐标。
                        occupied.Add(rect);
                    }
                }
                if (!moved) break;
            }
        }

        /// 推挤记录：同一面板被同一展开者连挤多次时保留最早的 From、更新最新的 To
        private void RecordPushback(string expanderId, string panelId, double fromX, double fromY, double toX, double toY)
        {
            Dictionary<string, PushbackRecord> map;
            if (!expandPushback.TryGetValue(expanderId, out map))
            {
                map = new Dictionary<string, PushbackRecord>(StringComparer.Ordinal);
                expandPushback[expanderId] = map;
            }
            PushbackRecord record;
            if (map.TryGetValue(panelId, out record)) { record.ToX = toX; record.ToY = toY; }
            else map[panelId] = new PushbackRecord { FromX = fromX, FromY = fromY, ToX = toX, ToY = toY };
        }

        /// 折叠还原：把上次因展开 expandedId 而被挤走、且此后未被用户接管（当前位置仍是
        /// 被挤到的位置，4px 容差）的面板送回原位；原位已被占用或越界则跳过该面板。
        private void RestorePushback(string expandedId)
        {
            Dictionary<string, PushbackRecord> pushed;
            if (!expandPushback.TryGetValue(expandedId, out pushed)) return;
            expandPushback.Remove(expandedId);
            if (pushed.Count == 0) return;
            double workWidth = host.WorkAreaWidth;
            double workHeight = host.WorkAreaHeight;
            // 占用集合：所有"非待还原"面板的当前占地（此时 expandedId 已在 collapsed 列表中，按折叠占地）
            List<PanelRect> occupied = new List<PanelRect>();
            foreach (CategoryData category in preferences.Categories)
            {
                if (pushed.ContainsKey(category.Id)) continue;
                PositionData pos;
                if (!positions.TryGetValue(category.Id, out pos)) continue;
                double width, height;
                PanelFootprint(category.Id, out width, out height);
                occupied.Add(new PanelRect(pos.X, pos.Y, width, height));
            }
            bool changed = false;
            foreach (KeyValuePair<string, PushbackRecord> pair in pushed)
            {
                PositionData current;
                if (!positions.TryGetValue(pair.Key, out current)) continue;
                PushbackRecord record = pair.Value;
                // 用户接管检测：当前位置偏离被挤落位 > 4px，说明用户拖过它，不还原
                if (Math.Abs(current.X - record.ToX) > 4 || Math.Abs(current.Y - record.ToY) > 4) continue;
                double width, height;
                PanelFootprint(pair.Key, out width, out height);
                // 原位越界（显示器可能已切换）则钳回工作区
                ZonePosition clamped = ClampPosition(new ZonePosition { X = record.FromX, Y = record.FromY }, new ZoneSize { Width = width, Height = height }, workWidth, workHeight);
                PanelRect target = new PanelRect(clamped.X, clamped.Y, width, height);
                if (PanelOverlapsAny(target, occupied))
                {
                    PanelRect? nearFree = FindFreeSpot(target, occupied, workWidth, workHeight);
                    if (nearFree.HasValue)
                    {
                        positions[pair.Key] = new PositionData { X = nearFree.Value.X, Y = nearFree.Value.Y };
                        occupied.Add(nearFree.Value);
                        changed = true;
                    }
                    continue;
                }
                positions[pair.Key] = new PositionData { X = clamped.X, Y = clamped.Y };
                occupied.Add(target);
                changed = true;
            }
            if (changed) MarkDirty();
        }

        /// 为缺少位置记录的分类分配空闲位（新建分类场景：默认位 28,82 被占用时顺延）
        private void AssignMissingPositions()
        {
            bool changed = false;
            List<PanelRect> occupied = new List<PanelRect>();
            foreach (CategoryData category in preferences.Categories)
            {
                PositionData pos;
                if (!positions.TryGetValue(category.Id, out pos)) continue;
                double width, height;
                PanelFootprint(category.Id, out width, out height);
                occupied.Add(new PanelRect(pos.X, pos.Y, width, height));
            }
            foreach (CategoryData category in preferences.Categories)
            {
                if (positions.ContainsKey(category.Id)) continue;
                double width, height;
                PanelFootprint(category.Id, out width, out height);
                PanelRect preferred = new PanelRect(28, 82, width, height);
                PanelRect? free = FindFreeSpot(preferred, occupied, host.WorkAreaWidth, host.WorkAreaHeight);
                if (!free.HasValue) free = preferred;
                positions[category.Id] = new PositionData { X = free.Value.X, Y = free.Value.Y };
                occupied.Add(free.Value);
                changed = true;
            }
            if (changed) MarkDirty();
        }

        private static void MergeModeDict(Dictionary<string, string> target, Dictionary<string, object> value)
        {
            if (value == null) return;
            foreach (KeyValuePair<string, object> pair in value)
            {
                object entry = pair.Value;
                if (entry == null) continue;
                string text = Convert.ToString(entry);
                if (text.Length == 0) continue;
                target[pair.Key] = text;
            }
        }

        private static void MergeItemLayouts(Dictionary<string, ItemLayoutData> target, Dictionary<string, object> value)
        {
            if (value == null) return;
            foreach (KeyValuePair<string, object> pair in value)
            {
                Dictionary<string, object> dict = pair.Value as Dictionary<string, object>;
                if (dict == null) continue;
                ItemLayoutData layout = new ItemLayoutData();
                double number;
                number = AsNumber(Get(dict, "itemSize"));
                if (!double.IsNaN(number) && !double.IsInfinity(number)) layout.ItemSize = Math.Max(44, Math.Min(112, number));
                number = AsNumber(Get(dict, "iconSize"));
                if (!double.IsNaN(number) && !double.IsInfinity(number)) layout.IconSize = Math.Max(24, Math.Min(72, number));
                number = AsNumber(Get(dict, "gap"));
                if (!double.IsNaN(number) && !double.IsInfinity(number)) layout.Gap = Math.Max(0, Math.Min(24, number));
                string alignment = AsString(Get(dict, "alignment"));
                if (alignment == "center" || alignment == "right") layout.Alignment = alignment;
                number = AsNumber(Get(dict, "columns"));
                if (!double.IsNaN(number) && !double.IsInfinity(number)) layout.Columns = Math.Max(0, Math.Min(8, (int)Math.Round(number)));
                object rawShow;
                if (dict.TryGetValue("showLabels", out rawShow) && rawShow is bool) layout.ShowLabels = (bool)rawShow;
                number = AsNumber(Get(dict, "labelSize"));
                if (!double.IsNaN(number) && !double.IsInfinity(number)) layout.LabelSize = Math.Max(8, Math.Min(14, number));
                target[pair.Key] = layout;
            }
        }

        // ---- 取值辅助（JavaScriptSerializer 解析结果：int/double/bool/string/object[]/Dictionary） ----
        private static object Get(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, object> AsDict(object value)
        {
            return value as Dictionary<string, object>;
        }

        private static string AsString(object value)
        {
            if (value == null) return "";
            if (value is string) return (string)value;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool AsBool(object value)
        {
            return value is bool && (bool)value;
        }

        /// 对齐 JS Boolean()：truthy 判定（normalizeCategory.acceptsFolders 用）
        private static bool AsTruthy(object value)
        {
            if (value == null) return false;
            if (value is bool) return (bool)value;
            if (value is string) return ((string)value).Length > 0;
            if (value is int) return (int)value != 0;
            if (value is long) return (long)value != 0;
            if (value is double) return (double)value != 0;
            if (value is float) return (float)value != 0;
            return true;
        }

        private static double AsNumber(object value)
        {
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is float) return (float)value;
            if (value is decimal) return (double)(decimal)value;
            return double.NaN;
        }

        private static double BoundedNumber(object value, double fallback, double minimum, double maximum)
        {
            double number = AsNumber(value);
            if (double.IsNaN(number) || double.IsInfinity(number)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, number));
        }

        // =============================================================
        // 主题计算（theme.ts）
        // =============================================================
        /// 对齐 resolveEffectiveTheme：system 时按系统浅色/深色偏好
        internal static string ResolveEffectiveTheme(string theme)
        {
            if (theme == "system") return SystemUsesDarkTheme() ? "dark" : "light";
            return theme;
        }

        private static bool SystemUsesDarkTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    object value = key.GetValue("AppsUseLightTheme");
                    if (value == null) return false;
                    int number;
                    if (int.TryParse(Convert.ToString(value), out number)) return number == 0;
                }
            }
            catch { }
            return false;
        }

        /// 对齐 resolveAccent
        internal static string ResolveAccent(string colorScheme, string customColor)
        {
            SchemeEntry entry;
            if (colorScheme == "custom" || !SchemeColors.TryGetValue(colorScheme, out entry)) return customColor;
            return entry.Accent;
        }

        /// 对齐 resolveSurfaceRgb
        internal static int[] ResolveSurfaceRgb(string colorScheme, string customColor, string effectiveTheme, string customSurfaceColor = null)
        {
            if (effectiveTheme == "dark") return new int[] { 34, 33, 30 };
            if (colorScheme == "white") return new int[] { 255, 255, 255 };
            SchemeEntry entry;
            if (colorScheme == "custom" || !SchemeColors.TryGetValue(colorScheme, out entry))
            {
                if (!String.IsNullOrEmpty(customSurfaceColor) && HexColorPattern.IsMatch(customSurfaceColor))
                {
                    return ColorToRgb(customSurfaceColor);
                }
                int[] rgb = ColorToRgb(customColor);
                return new int[]
                {
                    (int)Math.Floor(rgb[0] * 0.08 + 255 * 0.92 + 0.5),
                    (int)Math.Floor(rgb[1] * 0.08 + 255 * 0.92 + 0.5),
                    (int)Math.Floor(rgb[2] * 0.08 + 255 * 0.92 + 0.5),
                };
            }
            return new int[] { entry.Surface[0], entry.Surface[1], entry.Surface[2] };
        }

        /// 对齐 colorToRgb
        internal static int[] ColorToRgb(string value)
        {
            if (value == null) value = "";
            int hashIndex = value.IndexOf('#');
            if (hashIndex >= 0) value = value.Substring(0, hashIndex) + value.Substring(hashIndex + 1);
            if (value.Length > 6) value = value.Substring(0, 6);
            value = value.PadRight(6, '0');
            return new int[]
            {
                ParseHexByte(value.Substring(0, 2)),
                ParseHexByte(value.Substring(2, 2)),
                ParseHexByte(value.Substring(4, 2)),
            };
        }

        private static int ParseHexByte(string text)
        {
            int value;
            return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        /// 对齐 toHexColor
        internal static string ToHexColor(int[] rgb)
        {
            StringBuilder builder = new StringBuilder("#");
            foreach (int value in rgb)
            {
                int clamped = Math.Max(0, Math.Min(255, (int)Math.Floor(value + 0.5)));
                builder.Append(clamped.ToString("x2"));
            }
            return builder.ToString();
        }

        // =============================================================
        // 分类器（classifier.ts / magicColor.ts）
        // =============================================================
        internal static string GetExtension(string name)
        {
            int lastDot = name.LastIndexOf('.');
            return lastDot > 0 ? name.Substring(lastDot + 1).ToLowerInvariant() : "";
        }

        internal static string ClassifyKind(string extension, bool isDirectory)
        {
            if (isDirectory) return "folder";
            if (ShortcutExtensions.Contains(extension)) return "shortcut";
            if (ImageExtensions.Contains(extension)) return "image";
            if (MediaExtensions.Contains(extension)) return "media";
            if (ArchiveExtensions.Contains(extension)) return "archive";
            if (extension.Length > 0) return "document";
            return "other";
        }

        internal static string MatchCategory(string extension, bool isDirectory, List<CategoryData> categories)
        {
            foreach (CategoryData category in categories)
            {
                if (isDirectory ? category.AcceptsFolders : category.Extensions.Contains(extension)) return category.Id;
            }
            return UncategorizedId;
        }

        private string MatchRuleCategory(string name, string path, string extension, string fallbackCategoryId)
        {
            foreach (OrganizationRuleData rule in preferences.Rules)
            {
                if (!rule.Enabled || String.IsNullOrWhiteSpace(rule.Pattern)) continue;
                string pattern = rule.Pattern.Trim();
                bool matched = false;
                if (rule.Match == "extension")
                {
                    string[] parts = pattern.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string part in parts)
                    {
                        string normalized = part.Trim().TrimStart('.');
                        if (String.Equals(normalized, extension, StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
                    }
                }
                else if (rule.Match == "name") matched = WildcardMatch(name, pattern);
                else matched = WildcardMatch(path, pattern) || path.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                if (matched) return rule.CategoryId;
            }
            return fallbackCategoryId;
        }

        private static bool WildcardMatch(string value, string pattern)
        {
            try
            {
                string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return Regex.IsMatch(value ?? "", regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch { return String.Equals(value, pattern, StringComparison.OrdinalIgnoreCase); }
        }

        /// FNV-1a 32 位哈希 → base36（对齐 makeItemId）
        internal static string MakeItemId(string path)
        {
            uint hash = 2166136261;
            foreach (char c in path)
            {
                hash ^= (uint)c;
                hash *= 16777619;
            }
            return "item-" + ToBase36(hash);
        }

        /// 对齐 sortDesktopItems；附加路径决胜键保证排序稳定（JS sort 稳定）
        internal static List<ManagerItem> SortDesktopItems(List<ManagerItem> items, string mode)
        {
            List<ManagerItem> copy = new List<ManagerItem>(items);
            copy.Sort(delegate(ManagerItem left, ManagerItem right)
            {
                if (mode == "modified")
                {
                    double result = ParseIsoMillis(right.ModifiedAt) - ParseIsoMillis(left.ModifiedAt);
                    if (!double.IsNaN(result) && result != 0) return result > 0 ? 1 : -1;
                }
                if (mode == "size")
                {
                    double result = right.Size - left.Size;
                    if (result != 0) return result > 0 ? 1 : -1;
                }
                int nameResult = NaturalCompare(left.Name, right.Name);
                if (nameResult != 0) return nameResult;
                return string.CompareOrdinal(left.Path, right.Path);
            });
            return copy;
        }

        /// 近似 localeCompare('zh-CN', { numeric: true, sensitivity: 'base' })：
        /// 数字段按数值比较，文本段按 zh-CN 文化忽略大小写/变音比较
        /// 字符类别权重（实测 V8 zh-CN numeric+base 排序：符号 < 数字 < 汉字 < 拉丁字母）
        private static int CharRank(char c)
        {
            if (c >= '0' && c <= '9') return 1;
            if (IsCjk(c)) return 2;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) return 3;
            if (c >= 0x00C0 && c <= 0x024F) return 3;   // Latin-1/Extended 字母（é 等）
            return 0;                                   // 符号、标点、其他文字
        }

        private static bool IsCjk(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);
        }

        private static bool TextStartsWithSymbol(string segment)
        {
            return segment.Length > 0 && CharRank(segment[0]) == 0;
        }

        /// 文本段逐字符比较：先比字符类别权重，同类字符再按 zh-CN 文化规则（忽略大小写/变音）
        private static int CompareTextSegments(string a, string b)
        {
            int length = Math.Min(a.Length, b.Length);
            for (int i = 0; i < length; i++)
            {
                char ca = a[i];
                char cb = b[i];
                int rankA = CharRank(ca);
                int rankB = CharRank(cb);
                if (rankA != rankB) return rankA < rankB ? -1 : 1;
                if (rankA == 2 || rankA == 3)
                {
                    int result = ZhCompareInfo.Compare(ca.ToString(), cb.ToString(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
                    if (result != 0) return result;
                }
                else if (ca != cb)
                {
                    return ca < cb ? -1 : 1;
                }
            }
            return a.Length.CompareTo(b.Length);
        }

        private static int NaturalCompare(string left, string right)
        {
            MatchCollection leftTokens = NameTokenRegex.Matches(left);
            MatchCollection rightTokens = NameTokenRegex.Matches(right);
            int count = Math.Min(leftTokens.Count, rightTokens.Count);
            for (int i = 0; i < count; i++)
            {
                string a = leftTokens[i].Value;
                string b = rightTokens[i].Value;
                bool aNumeric = a.Length > 0 && a[0] >= '0' && a[0] <= '9';
                bool bNumeric = b.Length > 0 && b[0] >= '0' && b[0] <= '9';
                if (aNumeric && bNumeric)
                {
                    string trimmedA = a.TrimStart('0');
                    string trimmedB = b.TrimStart('0');
                    int result = trimmedA.Length.CompareTo(trimmedB.Length);
                    if (result == 0) result = string.CompareOrdinal(trimmedA, trimmedB);
                    if (result != 0) return result;
                    // 数值相同但前导零不同：短者优先，保持稳定
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                }
                else if (aNumeric != bNumeric)
                {
                    // 数字段 vs 文本段：文本段首字符为符号时符号在前，否则数字在前（V8 zh-CN）
                    if (aNumeric) return TextStartsWithSymbol(b) ? 1 : -1;
                    return TextStartsWithSymbol(a) ? -1 : 1;
                }
                else
                {
                    int result = CompareTextSegments(a, b);
                    if (result != 0) return result;
                }
            }
            return leftTokens.Count.CompareTo(rightTokens.Count);
        }

        /// 对齐 magicColor.ts dominantKindColor：占比最高的 kind → 主题色；空返回 null
        internal static string DominantKindColor(List<ManagerItem> items)
        {
            if (items == null || items.Count == 0) return null;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            List<string> order = new List<string>();
            foreach (ManagerItem item in items)
            {
                int count;
                if (counts.TryGetValue(item.Kind, out count)) counts[item.Kind] = count + 1;
                else { counts[item.Kind] = 1; order.Add(item.Kind); }
            }
            string dominant = "";
            int max = 0;
            foreach (string kind in order)
            {
                int count = counts[kind];
                if (count > max) { dominant = kind; max = count; }
            }
            string color;
            return KindToColor.TryGetValue(dominant, out color) ? color : null;
        }

        /// JS Number.toString(36)：.NET Convert.ToString 不支持基数 36，手写实现
        private static string ToBase36(uint value)
        {
            if (value == 0) return "0";
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            StringBuilder builder = new StringBuilder();
            while (value > 0)
            {
                builder.Insert(0, digits[(int)(value % 36)]);
                value /= 36;
            }
            return builder.ToString();
        }
        // ---- 时间辅助 ----
        private static string ToIsoDate(double millis)
        {
            try
            {
                if (!double.IsNaN(millis) && !double.IsInfinity(millis))
                    return UnixEpoch.AddMilliseconds(millis).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            }
            catch { }
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        private static double ParseIsoMillis(string value)
        {
            DateTime date;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date))
                return date.ToUniversalTime().Subtract(UnixEpoch).TotalMilliseconds;
            return double.NaN;
        }

        // =============================================================
        // 布局算法（panelLayout.ts 逐行移植）
        // =============================================================
        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        /// JS Math.round（四舍五入，非银行家舍入）
        private static double RoundJs(double value)
        {
            return Math.Floor(value + 0.5);
        }

        private static NormalizedViewport NormalizeViewport(PanelViewport viewport)
        {
            NormalizedViewport result = new NormalizedViewport();
            result.Width = Math.Max(1, viewport.Width);
            result.Height = Math.Max(1, viewport.Height);
            result.Margin = Clamp(viewport.Margin.HasValue ? viewport.Margin.Value : 24, 10, Math.Max(10, Math.Floor(result.Width / 4)));
            result.Top = Clamp(viewport.Top.HasValue ? viewport.Top.Value : result.Margin, 10, Math.Max(10, result.Height - 10));
            result.Bottom = Clamp(viewport.Bottom.HasValue ? viewport.Bottom.Value : result.Margin, 10, Math.Max(10, result.Height - result.Top));
            result.Gap = Clamp(viewport.Gap.HasValue ? viewport.Gap.Value : PanelLayoutGap, 0, 28);
            result.AvailableWidth = Math.Max(PanelMinWidth, result.Width - result.Margin * 2);
            result.AvailableHeight = Math.Max(PanelMinHeight, result.Height - result.Top - result.Bottom);
            return result;
        }

        private static GridShape ChooseGrid(int count, NormalizedViewport viewport, double maxPanelWidth = 320, double maxPanelHeight = 230, double maxColumns = double.PositiveInfinity)
        {
            int capacityColumns = Math.Max(1, (int)Math.Floor((viewport.AvailableWidth + viewport.Gap) / (PanelMinWidth + viewport.Gap)));
            int capacityRows = Math.Max(1, (int)Math.Floor((viewport.AvailableHeight + viewport.Gap) / (PanelMinHeight + viewport.Gap)));
            GridShape best = null;
            double bestScore = double.PositiveInfinity;
            int columnLimit = Math.Min(count, capacityColumns);
            if (maxColumns < columnLimit) columnLimit = (int)Math.Floor(maxColumns);
            for (int columns = 1; columns <= columnLimit; columns++)
            {
                int rows = (int)Math.Ceiling((double)count / columns);
                if (rows > capacityRows) continue;
                double cellWidth = Math.Floor((viewport.AvailableWidth - viewport.Gap * (columns - 1)) / columns);
                double cellHeight = Math.Floor((viewport.AvailableHeight - viewport.Gap * (rows - 1)) / rows);
                double panelWidth = Clamp(cellWidth, PanelMinWidth, maxPanelWidth);
                double panelHeight = Clamp(cellHeight, PanelMinHeight, maxPanelHeight);
                double ratioPenalty = Math.Abs(panelWidth / panelHeight - 1.36);
                double sizePenalty = Math.Abs(panelWidth - 292) / 292 + Math.Abs(panelHeight - 210) / 210;
                double emptyPenalty = (columns * rows - count) * 0.08;
                double score = ratioPenalty * 0.75 + sizePenalty * 0.22 + emptyPenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new GridShape();
                    best.Columns = columns;
                    best.Rows = rows;
                    best.PanelWidth = panelWidth;
                    best.PanelHeight = panelHeight;
                }
            }
            if (best != null) return best;
            GridShape fallback = new GridShape();
            fallback.Columns = Math.Max(1, Math.Min(count, capacityColumns));
            fallback.Rows = (int)Math.Ceiling((double)count / fallback.Columns);
            fallback.PanelWidth = PanelMinWidth;
            fallback.PanelHeight = PanelMinHeight;
            return fallback;
        }

        private static void SetPanel(LayoutResult result, string id, double x, double y, double width, double height)
        {
            ZonePosition position = new ZonePosition();
            position.X = RoundJs(x);
            position.Y = RoundJs(y);
            result.Positions[id] = position;
            ZoneSize size = new ZoneSize();
            size.Width = RoundJs(width);
            size.Height = RoundJs(height);
            result.Sizes[id] = size;
        }

        private static LayoutResult PlaceCenteredRows(List<string> ids, NormalizedViewport viewport, GridShape shape)
        {
            LayoutResult result = new LayoutResult();
            double groupHeight = shape.Rows * shape.PanelHeight + Math.Max(0, shape.Rows - 1) * viewport.Gap;
            double startY = viewport.Top + Math.Max(0, Math.Floor((viewport.AvailableHeight - groupHeight) / 2));
            for (int index = 0; index < ids.Count; index++)
            {
                int row = (int)Math.Floor((double)index / shape.Columns);
                int column = index % shape.Columns;
                int rowCount = Math.Min(shape.Columns, ids.Count - row * shape.Columns);
                double rowWidth = rowCount * shape.PanelWidth + Math.Max(0, rowCount - 1) * viewport.Gap;
                double startX = viewport.Margin + Math.Max(0, Math.Floor((viewport.AvailableWidth - rowWidth) / 2));
                SetPanel(result, ids[index],
                    startX + column * (shape.PanelWidth + viewport.Gap),
                    startY + row * (shape.PanelHeight + viewport.Gap),
                    shape.PanelWidth, shape.PanelHeight);
            }
            return result;
        }

        private static LayoutResult CreateBalancedLayout(List<string> ids, NormalizedViewport viewport)
        {
            LayoutResult result = new LayoutResult();
            List<string> leftIds = new List<string>();
            List<string> rightIds = new List<string>();
            for (int index = 0; index < ids.Count; index++)
            {
                if (index % 2 == 0) leftIds.Add(ids[index]);
                else rightIds.Add(ids[index]);
            }
            int sideCount = Math.Max(leftIds.Count, rightIds.Count);
            int rowCapacity = Math.Max(1, (int)Math.Floor((viewport.AvailableHeight + viewport.Gap) / (PanelMinHeight + viewport.Gap)));
            int rows = Math.Min(sideCount, rowCapacity);
            int columnsPerSide = Math.Max(1, (int)Math.Ceiling((double)sideCount / rows));
            double minimumCenter = Math.Max(72, Math.Min(420, RoundJs(viewport.AvailableWidth * 0.28)));
            double minimumRequiredWidth = PanelMinWidth * columnsPerSide * 2 + viewport.Gap * (Math.Max(0, columnsPerSide - 1) * 2 + 1);
            if (minimumRequiredWidth > viewport.AvailableWidth) return PlaceCenteredRows(ids, viewport, ChooseGrid(ids.Count, viewport));
            double widthForPanels = Math.Max(PanelMinWidth * 2 * columnsPerSide,
                viewport.AvailableWidth - minimumCenter - viewport.Gap * 2 * Math.Max(0, columnsPerSide - 1));
            double panelWidth = Clamp(Math.Floor(widthForPanels / (columnsPerSide * 2)), PanelMinWidth, 310);
            double panelHeight = Clamp(Math.Floor((viewport.AvailableHeight - viewport.Gap * Math.Max(0, rows - 1)) / rows), PanelMinHeight, 225);

            Action<List<string>, bool> placeSide = delegate(List<string> sideIds, bool right)
            {
                int sideRows = Math.Min(rows, Math.Max(1, sideIds.Count));
                double stackHeight = sideRows * panelHeight + Math.Max(0, sideRows - 1) * viewport.Gap;
                double startY = viewport.Top + Math.Max(0, Math.Floor((viewport.AvailableHeight - stackHeight) / 2));
                for (int index = 0; index < sideIds.Count; index++)
                {
                    int column = (int)Math.Floor((double)index / rows);
                    int row = index % rows;
                    double x = right
                        ? viewport.Width - viewport.Margin - panelWidth - column * (panelWidth + viewport.Gap)
                        : viewport.Margin + column * (panelWidth + viewport.Gap);
                    SetPanel(result, sideIds[index], x, startY + row * (panelHeight + viewport.Gap), panelWidth, panelHeight);
                }
            };
            placeSide(leftIds, false);
            placeSide(rightIds, true);
            return result;
        }

        private static LayoutResult CreateDockLayout(List<string> ids, NormalizedViewport viewport)
        {
            LayoutResult result = new LayoutResult();
            // 单列容量：按最小面板高度估算一列能放几个
            int columnCapacity = Math.Max(1, (int)Math.Floor((viewport.AvailableHeight + viewport.Gap) / (PanelMinHeight + viewport.Gap)));
            int columns = Math.Max(1, (int)Math.Ceiling((double)ids.Count / columnCapacity));
            // 宽度优先 304，窄屏按列数收缩；高度按单列容量均分（贴满列）
            double widthLimit = Math.Min(viewport.AvailableWidth, columns * 304 + Math.Max(0, columns - 1) * viewport.Gap);
            double panelWidth = Clamp(Math.Floor((widthLimit - Math.Max(0, columns - 1) * viewport.Gap) / columns), PanelMinWidth, 304);
            double panelHeight = Clamp(Math.Floor((viewport.AvailableHeight - Math.Max(0, columnCapacity - 1) * viewport.Gap) / columnCapacity), PanelMinHeight, 220);
            for (int index = 0; index < ids.Count; index++)
            {
                int column = (int)Math.Floor((double)index / columnCapacity);   // 先填满最右列
                int row = index % columnCapacity;
                double x = viewport.Width - viewport.Margin - panelWidth - column * (panelWidth + viewport.Gap);
                double y = viewport.Height - viewport.Bottom - panelHeight - row * (panelHeight + viewport.Gap); // 从底部向上堆叠
                SetPanel(result, ids[index], x, y, panelWidth, panelHeight);
            }
            return result;
        }

        private static LayoutResult CreateColumnLayout(List<string> ids, NormalizedViewport viewport)
        {
            GridShape shape = ChooseGrid(ids.Count, viewport, 320, 240, 3);
            int rowCapacity = Math.Max(1, (int)Math.Floor((viewport.AvailableHeight + viewport.Gap) / (PanelMinHeight + viewport.Gap)));
            if (shape.Rows > rowCapacity) shape = ChooseGrid(ids.Count, viewport, 320, 240);
            LayoutResult result = new LayoutResult();
            double groupWidth = shape.Columns * shape.PanelWidth + Math.Max(0, shape.Columns - 1) * viewport.Gap;
            double groupHeight = shape.Rows * shape.PanelHeight + Math.Max(0, shape.Rows - 1) * viewport.Gap;
            double startX = viewport.Margin + Math.Max(0, Math.Floor((viewport.AvailableWidth - groupWidth) / 2));
            double startY = viewport.Top + Math.Max(0, Math.Floor((viewport.AvailableHeight - groupHeight) / 2));
            for (int index = 0; index < ids.Count; index++)
            {
                int column = (int)Math.Floor((double)index / shape.Rows);
                int row = index % shape.Rows;
                SetPanel(result, ids[index],
                    startX + column * (shape.PanelWidth + viewport.Gap),
                    startY + row * (shape.PanelHeight + viewport.Gap),
                    shape.PanelWidth, shape.PanelHeight);
            }
            return result;
        }

        private static LayoutResult CreateCornerLayout(List<string> ids, NormalizedViewport viewport)
        {
            LayoutResult result = new LayoutResult();
            int layers = Math.Max(1, (int)Math.Ceiling((double)ids.Count / 4));
            double minimumRequiredWidth = PanelMinWidth * layers * 2 + viewport.Gap * Math.Max(1, layers * 2 - 1);
            if (minimumRequiredWidth > viewport.AvailableWidth) return PlaceCenteredRows(ids, viewport, ChooseGrid(ids.Count, viewport));
            double panelWidth = Clamp(Math.Floor((viewport.AvailableWidth - Math.Max(1, layers * 2 - 1) * viewport.Gap) / (layers * 2)), PanelMinWidth, 300);
            double panelHeight = Clamp(Math.Floor((viewport.AvailableHeight - viewport.Gap) / 2), PanelMinHeight, 220);
            for (int index = 0; index < ids.Count; index++)
            {
                int corner = index % 4;
                int layer = (int)Math.Floor((double)index / 4);
                bool fromLeft = corner == 0 || corner == 2;
                bool fromTop = corner < 2;
                double x = fromLeft
                    ? viewport.Margin + layer * (panelWidth + viewport.Gap)
                    : viewport.Width - viewport.Margin - panelWidth - layer * (panelWidth + viewport.Gap);
                double y = fromTop ? viewport.Top : viewport.Height - viewport.Bottom - panelHeight;
                SetPanel(result, ids[index], x, y, panelWidth, panelHeight);
            }
            return result;
        }

        internal static LayoutResult CreatePresetLayout(List<string> ids, string preset, PanelViewport rawViewport)
        {
            NormalizedViewport viewport = NormalizeViewport(rawViewport);
            LayoutResult empty = new LayoutResult();
            if (ids.Count == 0) return empty;
            if (preset == "balanced") return CreateBalancedLayout(ids, viewport);
            if (preset == "right-dock") return CreateDockLayout(ids, viewport);
            if (preset == "columns") return CreateColumnLayout(ids, viewport);
            if (preset == "corners") return CreateCornerLayout(ids, viewport);
            return PlaceCenteredRows(ids, viewport, ChooseGrid(ids.Count, viewport));
        }

        internal static LayoutResult AlignPanelLayout(List<string> ids, Dictionary<string, ZoneSize> currentSizes, string alignment, PanelViewport rawViewport)
        {
            NormalizedViewport viewport = NormalizeViewport(rawViewport);
            LayoutResult result = new LayoutResult();
            List<List<RowEntry>> rows = new List<List<RowEntry>>();
            List<RowEntry> row = new List<RowEntry>();
            double rowWidth = 0;
            double maxHeight = PanelMinHeight;   // 统一行高：所有面板高度的最大值

            foreach (string id in ids)
            {
                ZoneSize current;
                if (!currentSizes.TryGetValue(id, out current)) current = new ZoneSize { Width = 292, Height = 238 };
                ZoneSize size = new ZoneSize();
                size.Width = Clamp(RoundJs(current.Width), PanelMinWidth, viewport.AvailableWidth);
                size.Height = Clamp(RoundJs(current.Height), PanelMinHeight, viewport.AvailableHeight);
                double nextWidth = row.Count > 0 ? rowWidth + viewport.Gap + size.Width : size.Width;
                if (row.Count > 0 && nextWidth > viewport.AvailableWidth)
                {
                    rows.Add(row);
                    row = new List<RowEntry>();
                    rowWidth = 0;
                }
                RowEntry entry = new RowEntry();
                entry.Id = id;
                entry.Size = size;
                row.Add(entry);
                rowWidth = rowWidth != 0 ? rowWidth + viewport.Gap + size.Width : size.Width;
                if (size.Height > maxHeight) maxHeight = size.Height;
            }
            if (row.Count > 0) rows.Add(row);

            // 整体垂直居中：分组高度 = 行数 × 统一行高 + 行间 gap
            double groupHeight = rows.Count * maxHeight + Math.Max(0, rows.Count - 1) * viewport.Gap;
            double y = viewport.Top + Math.Max(0, Math.Floor((viewport.AvailableHeight - groupHeight) / 2));
            foreach (List<RowEntry> entries in rows)
            {
                double width = 0;
                foreach (RowEntry entry in entries) width += entry.Size.Width;
                width += Math.Max(0, entries.Count - 1) * viewport.Gap;
                double x = alignment == "left"
                    ? viewport.Margin
                    : alignment == "right"
                        ? viewport.Width - viewport.Margin - width
                        : RoundJs((viewport.Width - width) / 2);
                foreach (RowEntry entry in entries)
                {
                    SetPanel(result, entry.Id, x, y, entry.Size.Width, entry.Size.Height);
                    x += entry.Size.Width + viewport.Gap;
                }
                y += maxHeight + viewport.Gap;
            }
            return result;
        }

        private sealed class RowEntry
        {
            public string Id;
            public ZoneSize Size;
        }

        /// 对齐 layout.ts clampPosition（zoom=1 的桌面原生场景）
        internal static ZonePosition ClampPosition(ZonePosition position, ZoneSize size, double workWidth, double workHeight)
        {
            double maxX = Math.Max(10, workWidth - size.Width - 10);
            double maxY = Math.Max(10, workHeight - size.Height - 10);
            ZonePosition result = new ZonePosition();
            result.X = Math.Max(10, Math.Min(position.X, maxX));
            result.Y = Math.Max(10, Math.Min(position.Y, maxY));
            return result;
        }

        /// 工作区视口（对齐 App.tsx：width max(800, innerWidth/zoom)，height max(560, ...)）
        private PanelViewport GetLayoutViewport()
        {
            PanelViewport viewport = new PanelViewport();
            viewport.Width = Math.Max(800, host.WorkAreaWidth);
            viewport.Height = Math.Max(560, host.WorkAreaHeight);
            viewport.Margin = 24;
            viewport.Top = 24;
            viewport.Bottom = 24;
            viewport.Gap = PanelLayoutGap;
            return viewport;
        }

        /// 自适应预设 / 文件夹对齐非 manual 时重排（resize 或 ApplyConfig 后）
        private void ReflowIfAuto(bool expandAll = false)
        {
            bool changed = false;
            lock (stateLock)
            {
                if (disposed) return;
                if (adaptivePreset == "manual" && folderAlignment == "manual") return;
                List<string> ids = new List<string>();
                foreach (CategoryData category in preferences.Categories) ids.Add(category.Id);
                PanelViewport viewport = GetLayoutViewport();
                if (adaptivePreset != "manual")
                {
                    LayoutResult next = CreatePresetLayout(ids, adaptivePreset, viewport);
                    positions.Clear();
                    CopyLayout(positions, next.Positions);
                    sizes.Clear();
                    CopyLayoutSizes(sizes, next.Sizes);
                    if (expandAll) collapsed.Clear();   // 仅切换到新预设时展开全部；缩放/尺寸变化保留当前折叠状态
                    changed = true;
                }
                else if (folderAlignment != "manual")
                {
                    Dictionary<string, ZoneSize> currentSizes = new Dictionary<string, ZoneSize>();
                    foreach (KeyValuePair<string, SizeData> pair in sizes)
                    {
                        ZoneSize size = new ZoneSize();
                        size.Width = pair.Value.Width;
                        size.Height = pair.Value.Height;
                        currentSizes[pair.Key] = size;
                    }
                    LayoutResult next = AlignPanelLayout(ids, currentSizes, folderAlignment, viewport);
                    positions.Clear();
                    CopyLayout(positions, next.Positions);
                    foreach (KeyValuePair<string, ZoneSize> pair in next.Sizes)
                    {
                        SizeData size;
                        if (!sizes.TryGetValue(pair.Key, out size)) size = new SizeData();
                        size.Width = pair.Value.Width;
                        size.Height = pair.Value.Height;
                        sizes[pair.Key] = size;
                    }
                    if (expandAll) collapsed.Clear();   // 仅切换到新对齐方式时展开全部；缩放/尺寸变化保留当前折叠状态
                    changed = true;
                }
                if (changed)
                {
                    // 自动布局计算的是理想矩形，工作区变窄或历史尺寸偏大时仍需做一次最终避让。
                    ResolvePanelOverlaps(null);
                    MarkDirty();
                }
            }
            if (changed) RequestPanelSync();
        }

        private static void CopyLayout(Dictionary<string, PositionData> target, Dictionary<string, ZonePosition> source)
        {
            foreach (KeyValuePair<string, ZonePosition> pair in source)
            {
                PositionData data = new PositionData();
                data.X = pair.Value.X;
                data.Y = pair.Value.Y;
                target[pair.Key] = data;
            }
        }

        private static void CopyLayoutSizes(Dictionary<string, SizeData> target, Dictionary<string, ZoneSize> source)
        {
            foreach (KeyValuePair<string, ZoneSize> pair in source)
            {
                SizeData data = new SizeData();
                data.Width = pair.Value.Width;
                data.Height = pair.Value.Height;
                target[pair.Key] = data;
            }
        }

        // =============================================================
        // 扫描编排（移植 scanDesktop + applyReferencePins）
        // =============================================================
        /// 在后台线程执行；返回扫描结果（已应用引用钉选）
        private List<ManagerItem> ScanDesktopInternal(bool refreshIcons)
        {
            List<ManagerItem> items = new List<ManagerItem>();
            string desktopPath = host.DesktopPath;

            // 桌面目录：排除两个受管根 + 隐藏过滤
            List<ManagerEntry> entries = host.ScanDirectory(desktopPath, refreshIcons);
            foreach (ManagerEntry entry in entries)
            {
                if (entry.Name == ManagedRootName || entry.Name == LegacyRootName) continue;
                if (!preferences.ShowHiddenFiles && (entry.Hidden || entry.Name.StartsWith("."))) continue;
                items.Add(ToDesktopItem(entry, desktopPath, false, null));
            }

            // 受管根：桌面\片刻收纳 与 桌面\轻屿收纳 下的分类目录（managed=true，categoryId=分类）
            foreach (string managedName in new string[] { ManagedRootName, LegacyRootName })
            {
                string root = Path.Combine(desktopPath, managedName);
                try
                {
                    List<ManagerEntry> folders = host.ReadDirectory(root);
                    foreach (ManagerEntry folder in folders)
                    {
                        if (folder.Type != "DIRECTORY") continue;
                        CategoryData category = FindCategoryByName(folder.Name);
                        string folderPath = Path.Combine(root, folder.Name);
                        List<ManagerEntry> managedEntries = host.ScanDirectory(folderPath, false);
                        foreach (ManagerEntry entry in managedEntries)
                        {
                            if (!preferences.ShowHiddenFiles && (entry.Hidden || entry.Name.StartsWith("."))) continue;
                            items.Add(ToDesktopItem(entry, folderPath, true, category != null ? category.Id : null));
                        }
                    }
                }
                catch
                {
                    // 受管根可选：首次收纳时才创建
                }
            }

            // 文件夹门户：镜像真实目录内容，managed=true 永不参与自动收纳
            foreach (CategoryData category in preferences.Categories)
            {
                string portalPath = category.PortalPath == null ? "" : category.PortalPath.Trim();
                if (portalPath.Length == 0) continue;
                try
                {
                    List<ManagerEntry> portalEntries = host.ScanDirectory(portalPath, false);
                    foreach (ManagerEntry entry in portalEntries)
                    {
                        if (entry.Name == ManagedRootName || entry.Name == LegacyRootName) continue;
                        if (!preferences.ShowHiddenFiles && (entry.Hidden || entry.Name.StartsWith("."))) continue;
                        items.Add(ToDesktopItem(entry, portalPath, true, category.Id));
                    }
                }
                catch
                {
                    // 门户目录不存在或扫描失败时跳过，不报错
                }
            }

            // 引用模式：钉选路径强制归属到对应分类；不在扫描范围内的路径忽略
            return ApplyReferencePins(items);
        }

        private CategoryData FindCategoryByName(string name)
        {
            foreach (CategoryData category in preferences.Categories)
            {
                if (category.Name == name) return category;
            }
            return null;
        }

        private ManagerItem ToDesktopItem(ManagerEntry entry, string parentPath, bool managed, string forcedCategoryId)
        {
            ManagerItem item = new ManagerItem();
            string path = Path.Combine(parentPath, entry.Name);
            bool isDirectory = entry.Type == "DIRECTORY";
            string extension = isDirectory ? "" : GetExtension(entry.Name);
            item.Id = MakeItemId(path);
            item.Name = entry.Name;
            item.Path = path;
            item.Extension = extension;
            item.Kind = ClassifyKind(extension, isDirectory);
            item.Size = entry.Size;
            item.ModifiedAt = ToIsoDate(entry.ModifiedAt);
            string defaultCategory = forcedCategoryId != null ? forcedCategoryId : MatchCategory(extension, isDirectory, preferences.Categories);
            item.CategoryId = managed ? defaultCategory : MatchRuleCategory(entry.Name, path, extension, defaultCategory);
            item.Managed = managed;
            item.IconUrl = entry.IconUrl;
            return item;
        }

        private List<ManagerItem> ApplyReferencePins(List<ManagerItem> items)
        {
            if (preferences.OrganizeMode != "reference") return items;
            Dictionary<string, string> pinOwner = new Dictionary<string, string>();
            foreach (KeyValuePair<string, List<string>> pair in preferences.ReferencePins)
            {
                foreach (string path in pair.Value)
                {
                    if (!pinOwner.ContainsKey(path)) pinOwner[path] = pair.Key;
                }
            }
            if (pinOwner.Count == 0) return items;
            foreach (ManagerItem item in items)
            {
                string categoryId;
                if (pinOwner.TryGetValue(item.Path, out categoryId) && categoryId != item.CategoryId)
                {
                    item.CategoryId = categoryId;
                }
            }
            return items;
        }

        // ---- 扫描编排（runScan 语义：防重入 + quiet 队列 + 完成后同步面板） ----
        private void RunScan(bool quiet)
        {
            lock (stateLock)
            {
                if (disposed) return;
                if (scanInFlight)
                {
                    if (!quiet) refreshQueued = true;
                    return;
                }
                scanInFlight = true;
            }
            bool refreshIcons = !quiet;
            bool quietCapture = quiet;
            try
            {
                Task.Run(delegate
                {
                    List<ManagerItem> result = null;
                    string error = null;
                    try { result = ScanDesktopInternal(refreshIcons); }
                    catch (Exception ex) { error = ex.Message; }
                    host.RunOnUi(delegate { FinishScan(quietCapture, result, error); });
                });
            }
            catch (Exception ex)
            {
                lock (stateLock) { scanInFlight = false; }
                host.Log("启动扫描失败：" + ex.Message);
            }
        }

        private void FinishScan(bool quiet, List<ManagerItem> result, string error)
        {
            if (disposed) return;
            lock (stateLock)
            {
                if (error == null && result != null) scanItems = result;
                scanInFlight = false;
                bool requeue = refreshQueued;
                refreshQueued = false;
                if (requeue)
                {
                    RunScan(false);
                    return;
                }
            }
            if (error != null)
            {
                host.Log("桌面扫描失败：" + error);
                if (!quiet) PostToast(error, "info");
            }
            else if (!quiet)
            {
                PostToast("桌面分区已更新", "success");
            }
            RequestPanelSync();
            MaybeAutoOrganize();   // 扫描完成后按自动收纳策略静默收纳（对齐前端 effect）
        }

        /// automaticOrganize 且 move 模式且无交互时，对稳定候选执行静默收纳
        private void MaybeAutoOrganize()
        {
            bool should;
            lock (stateLock)
            {
                should = !disposed && preferences.AutomaticOrganize && preferences.OrganizeMode == "move" && !interactionActive;
            }
            if (should) OrganizeNow(true);
        }

        // =============================================================
        // 组织收纳 / 撤销（移植 useDesktopScan.organizeNow / undoLastOrganization）
        // =============================================================
        internal void OrganizeNow(bool onlyStable)
        {
            List<OrganizationMove> plan = null;
            int previewCount = 0;
            int totalCount = 0;
            lock (stateLock)
            {
                if (disposed) return;
                if (preferences.OrganizeMode == "reference")
                {
                    // 自动触发静默跳过；手动触发时给出提示
                    if (!onlyStable) PostToast("引用模式下不移动文件，无需收纳", "info");
                    return;
                }
                if (organizingInFlight || interactionActive) return;
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                List<ManagerItem> pending = new List<ManagerItem>();
                foreach (ManagerItem item in scanItems)
                {
                    if (item.Managed) continue;
                    string classifiedCategory = MatchRuleCategory(item.Name, item.Path, item.Extension, item.CategoryId);
                    if (classifiedCategory == UncategorizedId) continue;
                    if (classifiedCategory != item.CategoryId) item.CategoryId = classifiedCategory;
                    pending.Add(item);
                }
                Dictionary<string, CandidateState> nextCandidates = new Dictionary<string, CandidateState>();
                List<ManagerItem> ready = new List<ManagerItem>();
                foreach (ManagerItem item in pending)
                {
                    string signature = item.Size.ToString(CultureInfo.InvariantCulture) + ":" + item.ModifiedAt;
                    CandidateState previous;
                    long stableSince = organizeCandidates.TryGetValue(item.Path, out previous) && previous.Signature == signature
                        ? previous.StableSince
                        : now;
                    nextCandidates[item.Path] = new CandidateState(signature, stableSince);
                    if (!onlyStable || now - stableSince >= (long)preferences.OrganizeDelaySeconds * 1000) ready.Add(item);
                }
                organizeCandidates = nextCandidates;
                if (ready.Count == 0) return;
                plan = BuildOrganizationPlan(ready);
                totalCount = plan.Count;
                previewCount = Math.Min(8, plan.Count);
            }

            if (!onlyStable)
            {
                StringBuilder preview = new StringBuilder();
                for (int i = 0; i < previewCount; i++)
                {
                    if (i > 0) preview.Append('\n');
                    preview.Append("• ").Append(plan[i].Item.Name);
                }
                string remainder = plan.Count > 8 ? "\n……另有 " + (plan.Count - 8) + " 个项目" : "";
                string message = "将移动 " + totalCount + " 个已匹配项目到“片刻收纳”：\n\n"
                    + preview + remainder + "\n\n是否继续？";
                if (!host.Confirm(message)) return;
            }

            lock (stateLock)
            {
                if (disposed || organizingInFlight || interactionActive) return;
                organizingInFlight = true;
            }
            List<OrganizationMove> planCapture = plan;
            bool onlyStableCapture = onlyStable;
            try
            {
                Task.Run(delegate { ExecuteAndReport(planCapture, onlyStableCapture); });
            }
            catch (Exception ex)
            {
                lock (stateLock) { organizingInFlight = false; }
                host.Log("启动收纳任务失败：" + ex.Message);
            }
        }

        /// buildOrganizationPlan：分类目标文件夹 = 桌面\片刻收纳\{分类名}
        private List<OrganizationMove> BuildOrganizationPlan(List<ManagerItem> ready)
        {
            string desktopPath = host.DesktopPath;
            string separator = desktopPath.Contains("\\") ? "\\" : "/";
            string root = desktopPath + separator + ManagedRootName;
            List<OrganizationMove> plan = new List<OrganizationMove>();
            foreach (ManagerItem item in ready)
            {
                CategoryData category = FindCategoryById(item.CategoryId);
                string folder = root + separator + (category != null ? category.Name : OtherCategoryName);
                OrganizationMove move = new OrganizationMove();
                move.Item = item;
                move.Source = item.Path;
                move.Destination = folder + separator + item.Name;
                move.FolderPath = folder;
                move.CategoryId = item.CategoryId;
                move.Status = "pending";
                plan.Add(move);
            }
            return plan;
        }

        private CategoryData FindCategoryById(string id)
        {
            foreach (CategoryData category in preferences.Categories)
            {
                if (category.Id == id) return category;
            }
            return null;
        }

        /// executeOrganization：唯一目标名 + 顺序移动；完成后写历史并回 UI 线程汇报
        private void ExecuteAndReport(List<OrganizationMove> plan, bool onlyStable)
        {
            string toastMessage = null;
            string toastType = "success";
            bool rescan = false;
            try
            {
                ExecuteOrganizationPlan(plan);
                int moved = 0;
                int failed = 0;
                foreach (OrganizationMove move in plan)
                {
                    if (move.Status == "moved") moved++;
                    else failed++;
                }
                if (moved > 0)
                {
                    List<HistoryEntry> history = new List<HistoryEntry>();
                    foreach (OrganizationMove move in plan)
                    {
                        if (move.Status != "moved") continue;
                        HistoryEntry entry = new HistoryEntry();
                        entry.Source = move.Source;
                        entry.Destination = move.Destination;
                        entry.Name = move.Item.Name;
                        history.Add(entry);
                    }
                    bool historySaved;
                    lock (stateLock)
                    {
                        organizationHistory = history;
                        operationHistory.Add(new List<HistoryEntry>(history));
                        while (operationHistory.Count > MaxOperationHistory) operationHistory.RemoveAt(0);
                        redoHistory.Clear();
                        historySaved = SaveConfigNow();
                    }
                    if (!onlyStable || !historySaved)
                    {
                        toastMessage = "已自动收纳 " + moved + " 个项目"
                            + (failed > 0 ? "，" + failed + " 个失败" : "")
                            + (historySaved ? "" : "，但撤销记录保存失败");
                        toastType = (failed > 0 || !historySaved) ? "info" : "success";
                    }
                    rescan = true;
                }
                if (failed > 0 && moved == 0)
                {
                    toastMessage = failed + " 个项目均未能收纳，请检查目录权限或文件占用";
                    toastType = "info";
                }
            }
            catch (Exception ex)
            {
                toastMessage = ex.Message;
                toastType = "info";
            }
            host.RunOnUi(delegate
            {
                if (disposed) return;
                lock (stateLock) { organizingInFlight = false; }
                if (toastMessage != null) PostToast(toastMessage, toastType);
                if (rescan) RunScan(true);
            });
        }

        private void ExecuteOrganizationPlan(List<OrganizationMove> plan)
        {
            string root = host.DesktopPath.Contains("\\") ? host.DesktopPath + "\\" + ManagedRootName : host.DesktopPath + "/" + ManagedRootName;
            host.CreateDirectory(root);
            foreach (OrganizationMove move in plan)
            {
                host.CreateDirectory(move.FolderPath);
                try
                {
                    string destination = UniqueDestination(move.Destination);
                    host.Move(move.Source, destination);
                    move.Destination = destination;
                    move.Status = "moved";
                }
                catch (Exception ex)
                {
                    move.Status = "failed";
                    move.Error = ex.Message;
                }
            }
        }

        /// 目标已存在时依次尝试 "名称 (1)"、"名称 (2)" ...（上限 999）
        private string UniqueDestination(string destination)
        {
            if (!host.PathExists(destination)) return destination;
            int extensionIndex = destination.LastIndexOf('.');
            int separatorIndex = Math.Max(destination.LastIndexOf('\\'), destination.LastIndexOf('/'));
            bool hasExtension = extensionIndex > separatorIndex;
            string baseName = hasExtension ? destination.Substring(0, extensionIndex) : destination;
            string extension = hasExtension ? destination.Substring(extensionIndex) : "";
            for (int suffix = 1; suffix <= 999; suffix++)
            {
                string candidate = baseName + " (" + suffix + ")" + extension;
                if (!host.PathExists(candidate)) return candidate;
            }
            throw new InvalidOperationException("目标文件夹中存在过多同名文件");
        }

        internal void UndoOrganization()
        {
            List<HistoryEntry> history = null;
            lock (stateLock)
            {
                if (disposed) return;
                if (preferences.OrganizeMode == "reference")
                {
                    PostToast("引用模式下不移动文件，无需收纳", "info");
                    return;
                }
                if (organizingInFlight || interactionActive) return;
                if (operationHistory.Count > 0) history = new List<HistoryEntry>(operationHistory[operationHistory.Count - 1]);
                else history = new List<HistoryEntry>(organizationHistory);
                if (history.Count == 0)
                {
                    PostToast("没有可撤销的收纳记录", "info");
                    return;
                }
                string message = "将尝试把上次收纳的 " + history.Count + " 个项目放回原位置，是否继续？";
                if (!host.Confirm(message)) return;
                organizingInFlight = true;
            }
            List<HistoryEntry> historyCapture = history;
            try
            {
                Task.Run(delegate { UndoExecute(historyCapture); });
            }
            catch (Exception ex)
            {
                lock (stateLock) { organizingInFlight = false; }
                host.Log("启动撤销任务失败：" + ex.Message);
            }
        }

        /// restoreOrganization：逆序放回原位置，失败项保留在历史中供下次重试
        private void UndoExecute(List<HistoryEntry> history)
        {
            List<HistoryEntry> remaining = new List<HistoryEntry>();
            int restored = 0;
            string toastMessage = null;
            string toastType = "info";
            try
            {
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    HistoryEntry entry = history[i];
                    if (!host.PathExists(entry.Destination))
                    {
                        remaining.Add(entry);   // 原收纳文件已不存在
                        continue;
                    }
                    try
                    {
                        string restoredTo = UniqueDestination(entry.Source);
                        host.Move(entry.Destination, restoredTo);
                        restored++;
                    }
                    catch
                    {
                        remaining.Add(entry);
                    }
                }
                lock (stateLock)
                {
                    organizationHistory = remaining;
                    if (operationHistory.Count > 0) operationHistory.RemoveAt(operationHistory.Count - 1);
                    if (remaining.Count > 0) operationHistory.Add(new List<HistoryEntry>(remaining));
                    else redoHistory.Add(new List<HistoryEntry>(history));
                    SaveConfigNow();
                }
                toastMessage = "已撤销 " + restored + " 个项目" + (remaining.Count > 0 ? "，" + remaining.Count + " 个失败" : "");
                toastType = remaining.Count > 0 ? "info" : "success";
            }
            catch (Exception ex)
            {
                toastMessage = ex.Message;
            }
            host.RunOnUi(delegate
            {
                if (disposed) return;
                lock (stateLock) { organizingInFlight = false; }
                if (toastMessage != null) PostToast(toastMessage, toastType);
                if (restored > 0) RunScan(true);
            });
        }

        internal void RedoOrganization()
        {
            List<HistoryEntry> history;
            lock (stateLock)
            {
                if (disposed || preferences.OrganizeMode == "reference" || organizingInFlight || interactionActive) return;
                if (redoHistory.Count == 0)
                {
                    PostToast("没有可重做的操作", "info");
                    return;
                }
                history = new List<HistoryEntry>(redoHistory[redoHistory.Count - 1]);
                if (!host.Confirm("将重做上一次撤销的 " + history.Count + " 个项目，是否继续？")) return;
                organizingInFlight = true;
            }
            try { Task.Run(delegate { RedoExecute(history); }); }
            catch (Exception ex)
            {
                lock (stateLock) { organizingInFlight = false; }
                host.Log("启动重做任务失败：" + ex.Message);
            }
        }

        private void RedoExecute(List<HistoryEntry> history)
        {
            int moved = 0;
            List<HistoryEntry> reapplied = new List<HistoryEntry>();
            string message = null;
            try
            {
                foreach (HistoryEntry entry in history)
                {
                    if (!host.PathExists(entry.Source)) continue;
                    try
                    {
                        string destination = UniqueDestination(entry.Destination);
                        host.Move(entry.Source, destination);
                        reapplied.Add(new HistoryEntry { Source = entry.Source, Destination = destination, Name = entry.Name });
                        moved++;
                    }
                    catch { }
                }
                lock (stateLock)
                {
                    if (redoHistory.Count > 0) redoHistory.RemoveAt(redoHistory.Count - 1);
                    if (reapplied.Count > 0)
                    {
                        operationHistory.Add(reapplied);
                        while (operationHistory.Count > MaxOperationHistory) operationHistory.RemoveAt(0);
                        organizationHistory = reapplied;
                    }
                    SaveConfigNow();
                }
                message = moved > 0 ? "已重做 " + moved + " 个项目" : "没有可重做的项目";
            }
            catch (Exception ex) { message = ex.Message; }
            host.RunOnUi(delegate
            {
                if (disposed) return;
                lock (stateLock) { organizingInFlight = false; }
                PostToast(message, moved > 0 ? "success" : "info");
                if (moved > 0) RunScan(true);
            });
        }

        // =============================================================
        // 面板负载构建（移植 usePanelSync）
        // =============================================================
        private string GetViewMode(string categoryId)
        {
            string mode;
            return viewModes.TryGetValue(categoryId, out mode) ? mode : "grid";
        }

        private string GetSortMode(string categoryId)
        {
            string mode;
            return sortModes.TryGetValue(categoryId, out mode) ? mode : "name";
        }

        private ItemLayoutData GetItemLayout(string categoryId)
        {
            ItemLayoutData layout;
            return itemLayouts.TryGetValue(categoryId, out layout) ? layout : new ItemLayoutData();
        }

        /// 构建面板负载；无变化（syncKey 相同）返回 null
        private PanelSyncData[] BuildPanels()
        {
            string effectiveTheme = ResolveEffectiveTheme(preferences.Theme);
            string panelThemeAccent = ResolveAccent(preferences.ColorScheme, preferences.CustomColor);
            string panelHeaderSurface = ToHexColor(ResolveSurfaceRgb(preferences.ColorScheme, preferences.CustomColor, effectiveTheme, preferences.CustomSurfaceColor));

            // 按分类分组
            Dictionary<string, List<ManagerItem>> itemsByCategory = new Dictionary<string, List<ManagerItem>>();
            foreach (ManagerItem item in scanItems)
            {
                List<ManagerItem> list;
                if (!itemsByCategory.TryGetValue(item.CategoryId, out list))
                {
                    list = new List<ManagerItem>();
                    itemsByCategory[item.CategoryId] = list;
                }
                list.Add(item);
            }

            HashSet<string> liveItemIds = new HashSet<string>();
            List<PanelSyncData> panels = new List<PanelSyncData>();
            foreach (CategoryData category in preferences.Categories)
            {
                PositionData position;
                if (!positions.TryGetValue(category.Id, out position)) position = new PositionData { X = 28, Y = 82 };
                SizeData size;
                if (!sizes.TryGetValue(category.Id, out size)) size = new SizeData { Width = 292, Height = 238 };

                List<ManagerItem> source;
                if (!itemsByCategory.TryGetValue(category.Id, out source)) source = new List<ManagerItem>();
                List<ManagerItem> panelItems = SortDesktopItems(source, GetSortMode(category.Id));

                // Magic 分区色：按内容占比最高的类型覆盖该分区颜色，胶囊/色块/标题条跟随内容
                string magic = preferences.MagicColor ? DominantKindColor(panelItems) : null;
                ItemLayoutData layout = GetItemLayout(category.Id);

                PanelSyncData panel = new PanelSyncData();
                panel.id = category.Id;
                panel.name = category.Name;
                panel.color = magic ?? category.Color;
                panel.themeAccent = magic != null ? ResolveAccent("custom", magic) : panelThemeAccent;
                panel.headerSurface = (preferences.ColorScheme == "white" || preferences.ColorScheme == "custom")
                    ? panelHeaderSurface
                    : (magic != null ? ToHexColor(ResolveSurfaceRgb("custom", magic, effectiveTheme, preferences.CustomSurfaceColor)) : panelHeaderSurface);
                panel.glassOpacity = preferences.GlassOpacity;
                panel.material = preferences.Material;
                panel.theme = effectiveTheme;
                panel.compact = preferences.CompactView;
                panel.capsuleMode = preferences.CapsuleMode;
                panel.categoryIcon = category.Icon;
                panel.readOnly = !String.IsNullOrWhiteSpace(category.PortalPath);
                panel.showExtensions = preferences.ShowExtensions;
                panel.labelPosition = preferences.LabelPosition;
                panel.autoHide = preferences.AutoHide;
                panel.autoHideDelaySeconds = preferences.AutoHideDelaySeconds;
                panel.viewMode = GetViewMode(category.Id);
                panel.sortMode = GetSortMode(category.Id);
                panel.itemSize = layout.ItemSize;
                panel.iconSize = layout.IconSize;
                panel.itemGap = layout.Gap;
                panel.itemAlignment = layout.Alignment;
                panel.itemColumns = layout.Columns;
                panel.showLabels = layout.ShowLabels;
                panel.labelSize = layout.LabelSize;
                panel.x = position.X;
                panel.y = position.Y;
                panel.width = size.Width;
                panel.height = size.Height;
                panel.pinned = pinned.Contains(category.Id);
                panel.collapsed = collapsed.Contains(category.Id);

                PanelItemData[] items = new PanelItemData[panelItems.Count];
                for (int i = 0; i < panelItems.Count; i++)
                {
                    ManagerItem item = panelItems[i];
                    liveItemIds.Add(category.Id + ":" + item.Id);
                    PanelItemData data = new PanelItemData();
                    data.id = item.Id;
                    data.name = item.Name;
                    data.path = item.Path;
                    data.iconUrl = item.IconUrl;
                    data.extension = item.Extension;
                    data.modifiedAt = item.ModifiedAt;
                    data.size = item.Size;
                    data.kind = item.Kind;
                    data.readOnly = item.Managed || preferences.OrganizeMode == "reference";
                    data.favorite = preferences.Favorites.Contains(item.Path);
                    data.pinned = preferences.PinnedItems.Contains(item.Path);
                    items[i] = data;
                }
                panel.items = items;

                // revision：面板内容变化才自增
                string panelKey = BuildPanelKey(panel);
                PanelRevision previous;
                if (!panelRevisions.TryGetValue(category.Id, out previous) || previous.Key != panelKey)
                {
                    panelRevisions[category.Id] = new PanelRevision(panelKey, (previous != null ? previous.Revision : 0) + 1);
                }
                panel.revision = panelRevisions[category.Id].Revision;
                panels.Add(panel);
            }

            // 清理已不存在的分类 revision 与已不存在的项目图标记录
            HashSet<string> liveIds = new HashSet<string>();
            foreach (CategoryData category in preferences.Categories) liveIds.Add(category.Id);
            List<string> staleRevisions = new List<string>();
            foreach (string id in panelRevisions.Keys)
            {
                if (!liveIds.Contains(id)) staleRevisions.Add(id);
            }
            foreach (string id in staleRevisions) panelRevisions.Remove(id);
            List<string> staleIcons = new List<string>();
            foreach (string id in sentIcons.Keys)
            {
                if (!liveItemIds.Contains(id)) staleIcons.Add(id);
            }
            foreach (string id in staleIcons) sentIcons.Remove(id);

            // 同步键基于完整数据（含图标）：未变化则无需重复同步
            string syncKey = BuildSyncKey(panels);
            if (syncKey == lastSyncKey) return null;

            // 真正发送的负载剔除未变化项目的 base64 图标（PanelWindow 复用旧图标），大幅减少传输体积
            foreach (PanelSyncData panel in panels)
            {
                if (panel.items == null) continue;
                foreach (PanelItemData item in panel.items)
                {
                    string iconKey = panel.id + ":" + item.id;
                    string sent;
                    if (item.iconUrl != null && sentIcons.TryGetValue(iconKey, out sent) && sent == item.iconUrl) item.iconUrl = null;
                }
            }
            // 记录本轮实际携带的图标（已发送）
            foreach (PanelSyncData panel in panels)
            {
                if (panel.items == null) continue;
                foreach (PanelItemData item in panel.items)
                {
                    string iconKey = panel.id + ":" + item.id;
                    if (item.iconUrl != null) sentIcons[iconKey] = item.iconUrl;
                }
            }
            lastSyncKey = syncKey;
            return panels.ToArray();
        }

        /// 对齐 buildPanelSyncKey（单面板）
        private static string BuildPanelKey(PanelSyncData panel)
        {
            return BuildItemKey(panel) + "|" + panel.id + "|" + panel.name + "|" + panel.color + "|" + panel.themeAccent
                + "|" + panel.headerSurface + "|" + FormatNumber(panel.glassOpacity) + "|" + panel.theme
                + "|" + panel.material
                + "|" + (panel.capsuleMode ? "true" : "false")
                + "|" + (panel.compact ? "true" : "false") + "|" + panel.viewMode + "|" + panel.sortMode
                + "|" + FormatNumber(panel.itemSize) + "|" + FormatNumber(panel.iconSize) + "|" + FormatNumber(panel.itemGap)
                + "|" + panel.itemAlignment + "|" + panel.itemColumns + "|" + (panel.showLabels ? "true" : "false")
                + "|" + FormatNumber(panel.labelSize) + "|" + FormatNumber(panel.x) + "|" + FormatNumber(panel.y)
                + "|" + FormatNumber(panel.width) + "|" + FormatNumber(panel.height)
                + "|" + (panel.showExtensions ? "true" : "false") + "|" + panel.labelPosition
                + "|" + (panel.autoHide ? "true" : "false") + "|" + panel.autoHideDelaySeconds
                + "|" + (panel.readOnly ? "true" : "false")
                + "|" + (panel.pinned ? "true" : "false") + "|" + (panel.collapsed ? "true" : "false");
        }

        private static string BuildItemKey(PanelSyncData panel)
        {
            StringBuilder builder = new StringBuilder();
            if (panel.items != null)
            {
                for (int i = 0; i < panel.items.Length; i++)
                {
                    if (i > 0) builder.Append(',');
                    PanelItemData item = panel.items[i];
                    builder.Append(item.id).Append(':').Append(item.name).Append(':').Append(item.modifiedAt)
                        .Append(':').Append(FormatNumber(item.size)).Append(':').Append(HashPanelValue(item.iconUrl))
                        .Append(':').Append(item.readOnly ? "true" : "false")
                        .Append(':').Append(item.favorite ? "true" : "false")
                        .Append(':').Append(item.pinned ? "true" : "false");
                }
            }
            return builder.ToString();
        }

        private static string BuildSyncKey(List<PanelSyncData> panels)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < panels.Count; i++)
            {
                if (i > 0) builder.Append("//");
                builder.Append(BuildPanelKey(panels[i]));
            }
            return builder.ToString();
        }

        /// 对齐 hashPanelValue（FNV-1a → base36；空串返回 "0"）
        internal static string HashPanelValue(string value)
        {
            if (String.IsNullOrEmpty(value)) return "0";
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash ^= (uint)c;
                hash *= 16777619;
            }
            return ToBase36(hash);
        }

        private static string FormatNumber(double value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// 状态变更 → 24ms 防抖同步面板（对齐前端 syncTimer）
        private void RequestPanelSync()
        {
            lock (stateLock)
            {
                if (disposed) return;
                if (syncTimer == null) syncTimer = new Timer(OnSyncTimer, null, PanelSyncDebounceMs, Timeout.Infinite);
                else syncTimer.Change(PanelSyncDebounceMs, Timeout.Infinite);
            }
        }

        private void OnSyncTimer(object state)
        {
            host.RunOnUi(delegate
            {
                lock (stateLock) { DisposeTimer(ref syncTimer); }
                SyncPanelsNow();
            });
        }

        private void SyncPanelsNow()
        {
            PanelSyncData[] panels;
            lock (stateLock)
            {
                if (disposed) return;
                panels = BuildPanels();
            }
            if (panels == null) return;
            host.SyncPanels(panels);
        }

        // =============================================================
        // 事件处理（移植 useNativeShell / App.tsx 面板事件）
        // =============================================================
        internal void SetInteractionActive(bool active)
        {
            lock (stateLock) { interactionActive = active; }
        }

        internal void ToggleClickThrough()
        {
            bool next;
            lock (stateLock)
            {
                if (disposed) return;
                clickThrough = !clickThrough;
                next = clickThrough;
            }
            host.SetClickThrough(next);
        }

        internal void ShowAll(bool visible)
        {
            host.SetPanelsVisible(visible);
        }

        internal void CollapseAll(bool collapse)
        {
            lock (stateLock)
            {
                if (disposed) return;
                collapsed = collapse ? new List<string>(AllCategoryIds()) : new List<string>();
                if (!collapse) ResolvePanelOverlaps(null);
                MarkDirty();
            }
            RequestPanelSync();
        }

        private List<string> AllCategoryIds()
        {
            List<string> ids = new List<string>();
            foreach (CategoryData category in preferences.Categories) ids.Add(category.Id);
            return ids;
        }

        /// 托盘命令：settings 与 new-category 由宿主直接处理（打开设置/分类弹窗）
        internal void HandleTrayCommand(string command)
        {
            if (command == "collapse-all") CollapseAll(true);
            else if (command == "expand-all") CollapseAll(false);
            else if (command == "scan") RunScan(false);
            else if (command == "organize") OrganizeNow(false);
            else if (command == "undo-organize") UndoOrganization();
            else if (command == "redo-organize") RedoOrganization();
            else if (command.StartsWith("workspace-", StringComparison.Ordinal)) HandleWorkspaceCommand(command);
            else if (command == "click-through") ToggleClickThrough();
            else host.Log("未知托盘命令：" + command);
        }

        /// 面板事件（panelPin/panelCollapse 的 value 是 id 字符串；其余是 {字段} 字典）
        internal void HandlePanelEvent(string name, object value)
        {
            if (name == "panelPin")
            {
                string id = ToPanelId(value);
                if (id.Length == 0) return;
                lock (stateLock)
                {
                    if (disposed) return;
                    if (pinned.Contains(id)) pinned.Remove(id);
                    else pinned.Add(id);
                    MarkDirty();
                }
                RequestPanelSync();
            }
            else if (name == "panelCollapse")
            {
                string id = ToPanelId(value);
                if (id.Length == 0) return;
                lock (stateLock)
                {
                    if (disposed) return;
                    bool wasCollapsed = collapsed.Contains(id);
                    if (wasCollapsed) collapsed.Remove(id);
                    else collapsed.Add(id);
                    if (wasCollapsed)
                    {
                        expandPushback.Remove(id);   // 新一轮展开：丢弃该面板上一轮的推挤记录
                        ResolvePanelOverlaps(id);    // 展开：让其它面板/胶囊就近让位，避免展开内容互相叠加
                    }
                    else
                    {
                        RestorePushback(id);         // 折叠：把被它挤走且未被用户接管的面板送回原位
                    }
                    MarkDirty();
                }
                RequestPanelSync();
            }
            else if (name == "panelRename")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail == null) return;
                RenameCategory(EventString(detail, "id"), EventString(detail, "name"));
            }
            else if (name == "panelView")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail == null) return;
                string id = EventString(detail, "id");
                string mode = EventString(detail, "mode");
                if (id.Length == 0 || mode.Length == 0) return;
                lock (stateLock)
                {
                    if (disposed) return;
                    viewModes[id] = mode;
                    MarkDirty();
                }
                RequestPanelSync();
            }
            else if (name == "panelSort")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail == null) return;
                string id = EventString(detail, "id");
                string mode = EventString(detail, "mode");
                if (id.Length == 0 || mode.Length == 0) return;
                lock (stateLock)
                {
                    if (disposed) return;
                    sortModes[id] = mode;
                    MarkDirty();
                }
                RequestPanelSync();
            }
            else if (name == "panelRefresh")
            {
                RunScan(false);
            }
            else if (name == "panelDrop")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail == null) return;
                HandlePanelDrop(detail);
            }
            else if (name == "itemFavorite" || name == "itemPin")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail == null) return;
                string path = EventString(detail, "path");
                if (path.Length == 0) return;
                ToggleItemMark(path, name == "itemFavorite");
            }
            else if (name == "itemOpened")
            {
                string path = value as string;
                if (!String.IsNullOrWhiteSpace(path)) MarkRecent(path);
            }
            else if (name == "panelBatchMove")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail != null) HandlePanelBatchMove(EventString(detail, "categoryId"), GetStringArray(detail, "paths"));
            }
            else if (name == "panelBatchRestore")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail != null) HandlePanelBatchRestore(GetStringArray(detail, "paths"));
            }
            else if (name == "panelGeometry")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail == null) return;
                string id = EventString(detail, "id");
                if (id.Length == 0) return;
                double x = AsNumber(Get(detail, "x"));
                double y = AsNumber(Get(detail, "y"));
                double width = AsNumber(Get(detail, "width"));
                double height = AsNumber(Get(detail, "height"));
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(width) || double.IsNaN(height)) return;
                lock (stateLock)
                {
                    if (disposed) return;
                    PositionData position;
                    if (!positions.TryGetValue(id, out position)) position = new PositionData();
                    position.X = x;
                    position.Y = y;
                    positions[id] = position;
                    SizeData size;
                    if (!sizes.TryGetValue(id, out size)) size = new SizeData();
                    // 防御：任何路径（含 DPI 缩放误写）都不允许把超出工作区的尺寸写入配置
                    double maxWidth = Math.Max(230, host.WorkAreaWidth);
                    double maxHeight = Math.Max(150, host.WorkAreaHeight);
                    size.Width = Math.Max(230, Math.Min(maxWidth, width));
                    size.Height = Math.Max(150, Math.Min(maxHeight, height));
                    sizes[id] = size;
                    adaptivePreset = "manual";   // 手动拖动后停止自动布局
                    folderAlignment = "manual";
                    MarkDirty();
                }
                RequestPanelSync();
            }
            else if (name == "panelItemSize")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail == null) return;
                string id = EventString(detail, "id");
                if (id.Length == 0) return;
                double itemSize = AsNumber(Get(detail, "itemSize"));
                double iconSize = AsNumber(Get(detail, "iconSize"));
                double labelSize = AsNumber(Get(detail, "labelSize"));
                if (double.IsNaN(itemSize) || double.IsNaN(iconSize) || double.IsNaN(labelSize)) return;
                lock (stateLock)
                {
                    if (disposed) return;
                    ItemLayoutData current = GetItemLayout(id);
                    ItemLayoutData next = new ItemLayoutData();
                    next.ItemSize = Math.Max(44, Math.Min(112, itemSize));
                    next.IconSize = Math.Max(24, Math.Min(72, iconSize));
                    next.Gap = current.Gap;
                    next.Alignment = current.Alignment;
                    next.Columns = current.Columns;
                    next.ShowLabels = current.ShowLabels;
                    next.LabelSize = Math.Max(8, Math.Min(14, labelSize));
                    itemLayouts[id] = next;
                    MarkDirty();
                }
                RequestPanelSync();
            }
            else if (name == "noteCreate")
            {
                string mode = "todo";
                string title = null;
                double? posX = null;
                double? posY = null;
                if (value is string)
                {
                    string s = (string)value;
                    if (s == "note" || s == "todo") mode = s;
                }
                else if (value is Dictionary<string, object>)
                {
                    Dictionary<string, object> dict = (Dictionary<string, object>)value;
                    mode = EventString(dict, "mode");
                    if (String.IsNullOrWhiteSpace(mode)) mode = "todo";
                    title = EventString(dict, "title");
                    if (dict.ContainsKey("x") && dict["x"] != null)
                    {
                        double xVal = AsNumber(dict["x"]);
                        if (!double.IsNaN(xVal)) posX = xVal;
                    }
                    if (dict.ContainsKey("y") && dict["y"] != null)
                    {
                        double yVal = AsNumber(dict["y"]);
                        if (!double.IsNaN(yVal)) posY = yVal;
                    }
                }
                CreateNoteWithMode(mode, title, posX, posY);
            }
            else if (name == "noteDelete")
            {
                string id = ToPanelId(value);
                if (id.Length > 0) DeleteNote(id);
            }
            else if (name == "notePin")
            {
                string id = ToPanelId(value);
                if (id.Length > 0) ToggleNotePin(id);
            }
            else if (name == "noteCollapse")
            {
                string id = ToPanelId(value);
                if (id.Length > 0) ToggleNoteCollapse(id);
            }
            else if (name == "noteGeometry")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail != null)
                {
                    string id = EventString(detail, "id");
                    double x = EventDouble(detail, "x", 100);
                    double y = EventDouble(detail, "y", 100);
                    double w = EventDouble(detail, "width", 260);
                    double h = EventDouble(detail, "height", 340);
                    if (id.Length > 0) UpdateNoteGeometry(id, x, y, w, h);
                }
            }
            else if (name == "noteUpdate")
            {
                Dictionary<string, object> detail = value as Dictionary<string, object>;
                if (detail != null) UpdateNoteFromDict(detail);
            }
            else
            {
                host.Log("未知面板事件：" + name);
            }
        }

        private static double EventDouble(Dictionary<string, object> dict, string key, double fallback)
        {
            object value;
            if (!dict.TryGetValue(key, out value) || value == null) return fallback;
            double num = AsNumber(value);
            return double.IsNaN(num) ? fallback : num;
        }

        internal void CreateNote()
        {
            CreateNoteWithMode("todo", null, null, null);
        }

        internal void CreateNoteWithMode(string mode, string title)
        {
            CreateNoteWithMode(mode, title, null, null);
        }

        internal void CreateNoteWithMode(string mode, string title, double? posX, double? posY)
        {
            NoteData note = new NoteData();
            note.Id = Guid.NewGuid().ToString("N");
            note.Mode = String.Equals(mode, "note", StringComparison.OrdinalIgnoreCase) ? "note" : "todo";
            if (!String.IsNullOrWhiteSpace(title))
            {
                note.Title = title;
            }
            else
            {
                note.Title = note.Mode == "note" ? "随手备忘" : "今日待办";
            }
            note.Color = "default";
            double baseX = 240;
            double baseY = 160;
            if (posX.HasValue && posY.HasValue && posX.Value > -2000 && posY.Value > -2000)
            {
                baseX = posX.Value;
                baseY = posY.Value;
            }
            else
            {
                lock (stateLock)
                {
                    if (notes.Count > 0)
                    {
                        baseX += (notes.Count % 5) * 32;
                        baseY += (notes.Count % 5) * 32;
                    }
                }
            }
            lock (stateLock)
            {
                note.X = baseX;
                note.Y = baseY;
                note.Width = 280;
                note.Height = 340;
                note.Pinned = false;
                note.Collapsed = false;
                note.NoteContent = "";
                notes.Add(note);
                MarkDirty();
            }
            RequestNotesSync();
        }

        internal void DeleteNote(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return;
            lock (stateLock)
            {
                int index = -1;
                for (int i = 0; i < notes.Count; i++)
                {
                    if (notes[i].Id == id) { index = i; break; }
                }
                if (index >= 0)
                {
                    notes.RemoveAt(index);
                    MarkDirty();
                }
            }
            RequestNotesSync();
        }

        internal void ToggleNotePin(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return;
            lock (stateLock)
            {
                foreach (NoteData note in notes)
                {
                    if (note.Id == id)
                    {
                        note.Pinned = !note.Pinned;
                        MarkDirty();
                        break;
                    }
                }
            }
            RequestNotesSync();
        }

        internal void ToggleNoteCollapse(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return;
            lock (stateLock)
            {
                foreach (NoteData note in notes)
                {
                    if (note.Id == id)
                    {
                        note.Collapsed = !note.Collapsed;
                        MarkDirty();
                        break;
                    }
                }
            }
            RequestNotesSync();
        }

        internal void UpdateNoteGeometry(string id, double x, double y, double width, double height)
        {
            if (String.IsNullOrWhiteSpace(id)) return;
            lock (stateLock)
            {
                foreach (NoteData note in notes)
                {
                    if (note.Id == id)
                    {
                        note.X = x;
                        note.Y = y;
                        note.Width = width;
                        note.Height = height;
                        MarkDirty();
                        break;
                    }
                }
            }
        }

        private void UpdateNoteFromDict(Dictionary<string, object> dict)
        {
            string id = EventString(dict, "id");
            if (String.IsNullOrWhiteSpace(id)) return;
            lock (stateLock)
            {
                foreach (NoteData note in notes)
                {
                    if (note.Id == id)
                    {
                        if (dict.ContainsKey("title")) note.Title = EventString(dict, "title");
                        if (dict.ContainsKey("mode")) note.Mode = EventString(dict, "mode");
                        if (dict.ContainsKey("color")) note.Color = EventString(dict, "color");
                        if (dict.ContainsKey("pinned")) note.Pinned = AsBool(Get(dict, "pinned"));
                        if (dict.ContainsKey("collapsed")) note.Collapsed = AsBool(Get(dict, "collapsed"));
                        if (dict.ContainsKey("noteContent")) note.NoteContent = AsString(Get(dict, "noteContent"));
                        if (dict.ContainsKey("todos"))
                        {
                            object rawTodos = Get(dict, "todos");
                            object[] todoArray = rawTodos as object[];
                            if (todoArray == null)
                            {
                                System.Collections.ArrayList todoList = rawTodos as System.Collections.ArrayList;
                                if (todoList != null) todoArray = todoList.ToArray();
                            }
                            if (todoArray != null)
                            {
                                note.Todos.Clear();
                                foreach (object t in todoArray)
                                {
                                    Dictionary<string, object> td = t as Dictionary<string, object>;
                                    if (td == null) continue;
                                    string tid = EventString(td, "id");
                                    if (String.IsNullOrWhiteSpace(tid)) tid = Guid.NewGuid().ToString("N");
                                    string text = EventString(td, "text");
                                    bool done = AsBool(Get(td, "done"));
                                    double createdAt = EventDouble(td, "createdAt", 0);
                                    note.Todos.Add(new TodoItemData { Id = tid, Text = text, Done = done, CreatedAt = createdAt });
                                }
                            }
                        }
                        MarkDirty();
                        break;
                    }
                }
            }
        }

        internal void RequestNotesSync()
        {
            List<NoteData> copy = new List<NoteData>();
            bool noteCapsuleMode = false;
            bool desktopContextMenu = true;
            lock (stateLock)
            {
                noteCapsuleMode = preferences.NoteCapsuleMode;
                desktopContextMenu = preferences.DesktopContextMenu;
                foreach (NoteData note in notes) copy.Add(note.Clone());
            }
            host.RunOnUi(delegate { host.SyncNotes(copy, noteCapsuleMode, desktopContextMenu); });
        }

        private static List<NoteData> NormalizeNotes(object raw)
        {
            List<NoteData> result = new List<NoteData>();
            if (raw == null)
            {
                // 首次配置且无历史便签时，生成一张示范便签
                NoteData welcome = new NoteData();
                welcome.Id = Guid.NewGuid().ToString("N");
                welcome.Title = "今日待办";
                welcome.Mode = "todo";
                welcome.Color = "#ffffff";
                welcome.X = 160;
                welcome.Y = 140;
                welcome.Width = 260;
                welcome.Height = 340;
                welcome.Pinned = true;
                welcome.Collapsed = false;
                welcome.NoteContent = "随手记录待办、灵感、备忘或草稿...";
                double now = DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                welcome.Todos.Add(new TodoItemData { Id = Guid.NewGuid().ToString("N"), Text = "🌟 欢迎使用桌面便签", Done = true, CreatedAt = now });
                welcome.Todos.Add(new TodoItemData { Id = Guid.NewGuid().ToString("N"), Text = "📝 支持待办清单与自由记事", Done = false, CreatedAt = now });
                welcome.Todos.Add(new TodoItemData { Id = Guid.NewGuid().ToString("N"), Text = "🎨 点击右上角调色盘切换纸张底色", Done = false, CreatedAt = now });
                welcome.Todos.Add(new TodoItemData { Id = Guid.NewGuid().ToString("N"), Text = "📌 点击图钉置顶，或贴在桌面底层", Done = false, CreatedAt = now });
                result.Add(welcome);
                return result;
            }

            object[] array = raw as object[];
            if (array == null)
            {
                System.Collections.ArrayList list = raw as System.Collections.ArrayList;
                if (list != null) array = list.ToArray();
            }
            if (array == null) return result;

            foreach (object item in array)
            {
                Dictionary<string, object> dict = item as Dictionary<string, object>;
                if (dict == null) continue;
                string id = AsString(Get(dict, "id"));
                if (String.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");
                NoteData note = new NoteData();
                note.Id = id;
                note.Title = AsString(Get(dict, "title"));
                if (String.IsNullOrWhiteSpace(note.Title)) note.Title = "便签";
                note.Mode = AsString(Get(dict, "mode"));
                if (note.Mode != "note") note.Mode = "todo";
                note.Color = AsString(Get(dict, "color"));
                if (String.IsNullOrWhiteSpace(note.Color)) note.Color = "#ffffff";
                note.X = BoundedNumber(Get(dict, "x"), 160, -5000, 20000);
                note.Y = BoundedNumber(Get(dict, "y"), 140, -5000, 20000);
                note.Width = BoundedNumber(Get(dict, "width"), 280, 280, 2000);
                double parsedHeight = BoundedNumber(Get(dict, "height"), 340, 30, 2000);
                if (parsedHeight <= 50) parsedHeight = 340;
                note.Height = parsedHeight;
                note.Pinned = AsBool(Get(dict, "pinned"));
                note.Collapsed = AsBool(Get(dict, "collapsed"));
                note.NoteContent = AsString(Get(dict, "noteContent"));

                object rawTodos = Get(dict, "todos");
                object[] todoArray = rawTodos as object[];
                if (todoArray == null)
                {
                    System.Collections.ArrayList todoList = rawTodos as System.Collections.ArrayList;
                    if (todoList != null) todoArray = todoList.ToArray();
                }
                if (todoArray != null)
                {
                    foreach (object t in todoArray)
                    {
                        Dictionary<string, object> td = t as Dictionary<string, object>;
                        if (td == null) continue;
                        string tid = AsString(Get(td, "id"));
                        if (String.IsNullOrWhiteSpace(tid)) tid = Guid.NewGuid().ToString("N");
                        string text = AsString(Get(td, "text"));
                        bool done = AsBool(Get(td, "done"));
                        double createdAt = AsNumber(Get(td, "createdAt"));
                        if (double.IsNaN(createdAt)) createdAt = 0;
                        note.Todos.Add(new TodoItemData { Id = tid, Text = text, Done = done, CreatedAt = createdAt });
                    }
                }
                result.Add(note);
            }
            return result;
        }

        /// panelPin / panelCollapse 的 detail 是 id 字符串
        private static string ToPanelId(object value)
        {
            if (value is string) return (string)value;
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            return dict != null ? EventString(dict, "id") : "";
        }

        private static string EventString(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!dict.TryGetValue(key, out value) || value == null) return "";
            return Convert.ToString(value);
        }

        /// 分类重命名：校验 → 重命名受管文件夹（两个根）→ 更新配置 → 重扫
        private void RenameCategory(string categoryId, string requestedName)
        {
            string toastMessage = null;
            string toastType = "success";
            bool changed = false;
            lock (stateLock)
            {
                if (disposed) return;
                CategoryData category = FindCategoryById(categoryId);
                if (category == null) return;
                try
                {
                    List<string> existingNames = new List<string>();
                    foreach (CategoryData candidate in preferences.Categories)
                    {
                        if (candidate.Id != categoryId) existingNames.Add(candidate.Name);
                    }
                    string name = NormalizeCategoryName(requestedName, existingNames);
                    if (name == category.Name) return;
                    // 重命名桌面\片刻收纳 与 桌面\轻屿收纳 下同名的受管文件夹
                    foreach (string rootName in new string[] { ManagedRootName, LegacyRootName })
                    {
                        string root = Path.Combine(host.DesktopPath, rootName);
                        string source = Path.Combine(root, category.Name);
                        string destination = Path.Combine(root, name);
                        if (!host.PathExists(source)) continue;
                        if (host.PathExists(destination)) throw new InvalidOperationException("已存在名为“" + name + "”的收纳文件夹");
                        host.Move(source, destination);
                    }
                    category.Name = name;
                    changed = true;
                    toastMessage = "分区已重命名为“" + name + "”";
                }
                catch (Exception ex)
                {
                    toastMessage = ex.Message;
                    toastType = "info";
                }
                if (changed) MarkDirty();
            }
            if (toastMessage != null) PostToast(toastMessage, toastType);
            if (changed) RunScan(true);
        }

        /// 拖文件进分区：引用模式钉选（不移动），移动模式收纳到分类文件夹
        private void HandlePanelDrop(Dictionary<string, object> detail)
        {
            string categoryId = EventString(detail, "categoryId");
            string[] paths = GetStringArray(detail, "paths");
            // 拖放意图（DeskBox NativeDropEffectPolicy）：panelDrop effect=move|copy，缺省 move
            // （旧事件/旧前端兼容）；copy = Ctrl 拖入或右键菜单选择复制，原文件保留在桌面
            string effect = EventString(detail, "effect");
            if (effect != "copy") effect = "move";
            if (categoryId.Length == 0 || paths.Length == 0) return;
            bool reference;
            bool portal;
            lock (stateLock)
            {
                if (disposed) return;
                reference = preferences.OrganizeMode == "reference";
                CategoryData category = FindCategoryById(categoryId);
                portal = category != null && !String.IsNullOrWhiteSpace(category.PortalPath);
            }
            if (portal)
            {
                PostToast("门户分区只引用文件，不会移动文件", "info");
                return;
            }
            if (reference)
            {
                lock (stateLock)
                {
                    if (disposed) return;
                    CategoryData category = FindCategoryById(categoryId);
                    string targetId = category != null ? category.Id : categoryId;
                    List<string> current;
                    if (!preferences.ReferencePins.TryGetValue(targetId, out current)) current = new List<string>();
                    HashSet<string> merged = new HashSet<string>(current, StringComparer.Ordinal);
                    foreach (string path in paths) merged.Add(path);
                    preferences.ReferencePins[targetId] = new List<string>(merged);
                    MarkDirty();
                }
                PostToast("已将 " + paths.Length + " 个项目加入分区（不移动文件）", "success");
                RunScan(true);   // 立即刷新视图归属
            }
            else
            {
                bool copy = effect == "copy";
                try
                {
                    MoveBatchResult result = copy ? host.CopyIntoCategory(categoryId, paths) : host.MoveIntoCategory(categoryId, paths);
                    // 复制不产生撤销历史（撤销复制意味着删除副本，超出“只移动不删除”的安全约定）
                    if (!copy && result.History.Count > 0)
                    {
                        lock (stateLock)
                        {
                            organizationHistory = new List<HistoryEntry>(result.History);
                            operationHistory.Add(new List<HistoryEntry>(result.History));
                            while (operationHistory.Count > MaxOperationHistory) operationHistory.RemoveAt(0);
                            redoHistory.Clear();
                            SaveConfigNow();
                        }
                    }
                    string message = (copy ? "已复制 " : "已收纳 ") + result.Moved + " 个项目";
                    if (result.Skipped > 0) message += "，跳过 " + result.Skipped + " 个";
                    if (result.Failed > 0) message += "，失败 " + result.Failed + " 个";
                    PostToast(message, result.Failed > 0 ? "info" : "success");
                }
                catch (Exception ex)
                {
                    PostToast(ex.Message, "info");
                }
                ScheduleDesktopRescan();   // 宿主若已通知 desktopChanged，280ms 防抖会合并为一次
            }
        }

        private void ToggleItemMark(string path, bool favorite)
        {
            lock (stateLock)
            {
                List<string> target = favorite ? preferences.Favorites : preferences.PinnedItems;
                int index = target.FindIndex(delegate(string value) { return String.Equals(value, path, StringComparison.OrdinalIgnoreCase); });
                if (index >= 0) target.RemoveAt(index);
                else target.Insert(0, path);
                while (target.Count > 120) target.RemoveAt(target.Count - 1);
                MarkDirty();
            }
            RequestPanelSync();
            PostToast((favorite ? "收藏" : "固定项目") + (IsMarked(path, favorite) ? "成功" : "已取消"), "success");
        }

        private bool IsMarked(string path, bool favorite)
        {
            lock (stateLock)
            {
                List<string> values = favorite ? preferences.Favorites : preferences.PinnedItems;
                return values.Contains(path);
            }
        }

        private void MarkRecent(string path)
        {
            lock (stateLock)
            {
                preferences.RecentItems.RemoveAll(delegate(string value) { return String.Equals(value, path, StringComparison.OrdinalIgnoreCase); });
                preferences.RecentItems.Insert(0, path);
                while (preferences.RecentItems.Count > 30) preferences.RecentItems.RemoveAt(preferences.RecentItems.Count - 1);
                MarkDirty();
            }
        }

        private void HandlePanelBatchMove(string categoryId, string[] paths)
        {
            if (String.IsNullOrWhiteSpace(categoryId) || paths == null || paths.Length == 0) return;
            bool reference;
            lock (stateLock) { reference = preferences.OrganizeMode == "reference"; }
            if (reference)
            {
                lock (stateLock)
                {
                    List<string> current;
                    if (!preferences.ReferencePins.TryGetValue(categoryId, out current)) current = new List<string>();
                    foreach (string path in paths) if (!current.Contains(path)) current.Add(path);
                    preferences.ReferencePins[categoryId] = current;
                    MarkDirty();
                }
                PostToast("已将 " + paths.Length + " 个项目加入分区（不移动文件）", "success");
                RunScan(true);
                return;
            }
            try
            {
                Task.Run(delegate
                {
                    MoveBatchResult result = host.MoveIntoCategory(categoryId, paths);
                    if (result.History.Count > 0)
                    {
                        lock (stateLock)
                        {
                            organizationHistory = new List<HistoryEntry>(result.History);
                            operationHistory.Add(new List<HistoryEntry>(result.History));
                            while (operationHistory.Count > MaxOperationHistory) operationHistory.RemoveAt(0);
                            redoHistory.Clear();
                            SaveConfigNow();
                        }
                    }
                    host.RunOnUi(delegate
                    {
                        string message = "已批量收纳 " + result.Moved + " 个项目";
                        if (result.Skipped > 0) message += "，跳过 " + result.Skipped + " 个";
                        if (result.Failed > 0) message += "，失败 " + result.Failed + " 个";
                        PostToast(message, result.Failed > 0 ? "info" : "success");
                        RunScan(true);
                    });
                });
            }
            catch (Exception ex) { PostToast(ex.Message, "info"); }
        }

        private void HandlePanelBatchRestore(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            Task.Run(delegate
            {
                MoveBatchResult result = new MoveBatchResult { Requested = paths.Length };
                foreach (string path in paths)
                {
                    if (String.IsNullOrWhiteSpace(path) || !host.PathExists(path)) { result.Skipped++; continue; }
                    try
                    {
                        string destination = UniqueDestination(Path.Combine(host.DesktopPath, Path.GetFileName(path)));
                        host.Move(path, destination);
                        result.Moved++;
                        result.History.Add(new HistoryEntry { Source = path, Destination = destination, Name = Path.GetFileName(path) });
                    }
                    catch (Exception ex) { result.Failed++; if (result.Failures.Count < 5) result.Failures.Add(ex.Message); }
                }
                if (result.History.Count > 0)
                {
                    lock (stateLock)
                    {
                        organizationHistory = new List<HistoryEntry>(result.History);
                        operationHistory.Add(new List<HistoryEntry>(result.History));
                        while (operationHistory.Count > MaxOperationHistory) operationHistory.RemoveAt(0);
                        redoHistory.Clear();
                        SaveConfigNow();
                    }
                }
                host.RunOnUi(delegate
                {
                    PostToast("已放回桌面 " + result.Moved + " 个项目" + (result.Failed > 0 ? "，失败 " + result.Failed + " 个" : ""), result.Failed > 0 ? "info" : "success");
                    if (result.Moved > 0) RunScan(true);
                });
            });
        }

        private void HandleWorkspaceCommand(string command)
        {
            if (command.StartsWith("workspace-save:", StringComparison.Ordinal)) SaveWorkspace(command.Substring("workspace-save:".Length));
            else if (command.StartsWith("workspace-activate:", StringComparison.Ordinal)) ActivateWorkspace(command.Substring("workspace-activate:".Length));
            else if (command.StartsWith("workspace-delete:", StringComparison.Ordinal)) DeleteWorkspace(command.Substring("workspace-delete:".Length));
            else if (command.StartsWith("workspace-preset:", StringComparison.Ordinal)) ApplyBuiltInWorkspace(command.Substring("workspace-preset:".Length));
        }

        private void ApplyBuiltInWorkspace(string preset)
        {
            lock (stateLock)
            {
                int count = preferences.Categories.Count;
                if (count == 0) return;
                double workWidth = Math.Max(640, host.WorkAreaWidth);
                double panelWidth = 292;
                double panelHeight = 238;
                double gap = 18;
                int columns = preset == "game" ? 2 : preset == "development" ? 3 : Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
                positions.Clear();
                sizes.Clear();
                collapsed.Clear();
                pinned.Clear();
                for (int i = 0; i < count; i++)
                {
                    int column = i % columns;
                    int row = i / columns;
                    double x;
                    double y = 28 + row * (panelHeight + gap);
                    if (preset == "game")
                    {
                        x = Math.Max(18, workWidth - (columns - column) * (panelWidth + gap));
                    }
                    else if (preset == "development")
                    {
                        x = 18 + column * (panelWidth + gap);
                    }
                    else
                    {
                        x = 18 + column * (panelWidth + gap);
                    }
                    positions[preferences.Categories[i].Id] = new PositionData { X = x, Y = y };
                    sizes[preferences.Categories[i].Id] = new SizeData { Width = panelWidth, Height = panelHeight };
                    viewModes[preferences.Categories[i].Id] = preset == "development" ? "list" : "grid";
                }
                adaptivePreset = "manual";
                folderAlignment = "manual";
                preferences.ActiveWorkspaceId = "";
                MarkDirty();
            }
            RequestPanelSync();
            PostToast("已切换到" + (preset == "office" ? "办公" : preset == "game" ? "游戏" : "开发") + "工作区", "success");
        }

        private WorkspaceData CaptureWorkspace(string id, string name)
        {
            WorkspaceData workspace = new WorkspaceData { Id = id, Name = name };
            foreach (KeyValuePair<string, PositionData> pair in positions) workspace.Positions[pair.Key] = new PositionData { X = pair.Value.X, Y = pair.Value.Y };
            foreach (KeyValuePair<string, SizeData> pair in sizes) workspace.Sizes[pair.Key] = new SizeData { Width = pair.Value.Width, Height = pair.Value.Height };
            workspace.Collapsed = new List<string>(collapsed);
            workspace.Pinned = new List<string>(pinned);
            foreach (KeyValuePair<string, string> pair in viewModes) workspace.ViewModes[pair.Key] = pair.Value;
            foreach (KeyValuePair<string, string> pair in sortModes) workspace.SortModes[pair.Key] = pair.Value;
            foreach (KeyValuePair<string, ItemLayoutData> pair in itemLayouts)
            {
                ItemLayoutData layout = pair.Value;
                workspace.ItemLayouts[pair.Key] = new ItemLayoutData { ItemSize = layout.ItemSize, IconSize = layout.IconSize, Gap = layout.Gap, Alignment = layout.Alignment, Columns = layout.Columns, ShowLabels = layout.ShowLabels, LabelSize = layout.LabelSize };
            }
            workspace.AdaptivePreset = adaptivePreset;
            workspace.FolderAlignment = folderAlignment;
            return workspace;
        }

        private void SaveWorkspace(string requestedName)
        {
            string name = (requestedName ?? "").Trim();
            if (name.Length == 0) { PostToast("请输入工作区名称", "info"); return; }
            if (name.Length > 20) name = name.Substring(0, 20);
            lock (stateLock)
            {
                WorkspaceData current = null;
                foreach (WorkspaceData candidate in preferences.Workspaces)
                    if (String.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) { current = candidate; break; }
                string id = current == null ? "" : current.Id;
                if (current == null)
                {
                    id = "workspace-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                    current = new WorkspaceData();
                    current.Id = id;
                    preferences.Workspaces.Add(current);
                }
                WorkspaceData snapshot = CaptureWorkspace(id, name);
                int index = preferences.Workspaces.IndexOf(current);
                preferences.Workspaces[index] = snapshot;
                preferences.ActiveWorkspaceId = id;
                MarkDirty();
            }
            PostToast("工作区“" + name + "”已保存", "success");
        }

        private void ActivateWorkspace(string id)
        {
            lock (stateLock)
            {
                WorkspaceData target = null;
                foreach (WorkspaceData candidate in preferences.Workspaces) if (candidate.Id == id) { target = candidate; break; }
                if (target == null) { PostToast("未找到该工作区", "info"); return; }
                positions.Clear(); foreach (KeyValuePair<string, PositionData> pair in target.Positions) positions[pair.Key] = new PositionData { X = pair.Value.X, Y = pair.Value.Y };
                sizes.Clear(); foreach (KeyValuePair<string, SizeData> pair in target.Sizes) sizes[pair.Key] = new SizeData { Width = pair.Value.Width, Height = pair.Value.Height };
                collapsed = new List<string>(target.Collapsed);
                pinned = new List<string>(target.Pinned);
                viewModes.Clear(); foreach (KeyValuePair<string, string> pair in target.ViewModes) viewModes[pair.Key] = pair.Value;
                sortModes.Clear(); foreach (KeyValuePair<string, string> pair in target.SortModes) sortModes[pair.Key] = pair.Value;
                itemLayouts.Clear(); foreach (KeyValuePair<string, ItemLayoutData> pair in target.ItemLayouts) itemLayouts[pair.Key] = pair.Value;
                adaptivePreset = target.AdaptivePreset;
                folderAlignment = target.FolderAlignment;
                preferences.ActiveWorkspaceId = id;
                MarkDirty();
            }
            RequestPanelSync();
            PostToast("已切换到工作区", "success");
        }

        private void DeleteWorkspace(string id)
        {
            lock (stateLock)
            {
                int index = preferences.Workspaces.FindIndex(delegate(WorkspaceData value) { return value.Id == id; });
                if (index < 0) return;
                preferences.Workspaces.RemoveAt(index);
                if (preferences.ActiveWorkspaceId == id) preferences.ActiveWorkspaceId = "";
                MarkDirty();
            }
            PostToast("工作区已删除", "success");
        }

        private static string[] GetStringArray(Dictionary<string, object> dict, string key)
        {
            object raw;
            if (!dict.TryGetValue(key, out raw) || raw == null) return new string[0];
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

        /// 桌面变更 → 280ms 防抖重扫（对齐前端 desktopChanged 处理）
        internal void HandleDesktopChanged()
        {
            ScheduleDesktopRescan();
        }

        private void ScheduleDesktopRescan()
        {
            lock (stateLock)
            {
                if (disposed) return;
                if (desktopRescanTimer == null) desktopRescanTimer = new Timer(OnDesktopRescanTimer, null, DesktopChangeDebounceMs, Timeout.Infinite);
                else desktopRescanTimer.Change(DesktopChangeDebounceMs, Timeout.Infinite);
            }
        }

        private void OnDesktopRescanTimer(object state)
        {
            host.RunOnUi(delegate
            {
                lock (stateLock) { DisposeTimer(ref desktopRescanTimer); }
                RunScan(true);
            });
        }

        /// 工作区尺寸变化 → 160ms 防抖重排（自适应布局 / 文件夹对齐）并收回越界面板
        internal void HandleResize()
        {
            lock (stateLock)
            {
                if (disposed) return;
                if (reflowTimer == null) reflowTimer = new Timer(OnReflowTimer, null, ResizeReflowDebounceMs, Timeout.Infinite);
                else reflowTimer.Change(ResizeReflowDebounceMs, Timeout.Infinite);
            }
        }

        private void OnReflowTimer(object state)
        {
            host.RunOnUi(delegate
            {
                lock (stateLock) { DisposeTimer(ref reflowTimer); }
                if (disposed) return;
                ClampAllPositions();
                ReflowIfAuto();
            });
        }

        /// 把越界的手动定位面板收回可视区（对齐前端 resize clampAll）
        private void ClampAllPositions()
        {
            bool changed = false;
            lock (stateLock)
            {
                double width = host.WorkAreaWidth;
                double height = host.WorkAreaHeight;
                foreach (KeyValuePair<string, PositionData> pair in positions)
                {
                    SizeData size;
                    if (!sizes.TryGetValue(pair.Key, out size)) size = new SizeData { Width = 292, Height = 238 };
                    ZonePosition position = new ZonePosition();
                    position.X = pair.Value.X;
                    position.Y = pair.Value.Y;
                    ZoneSize zoneSize = new ZoneSize();
                    zoneSize.Width = size.Width;
                    zoneSize.Height = size.Height;
                    ZonePosition clamped = ClampPosition(position, zoneSize, width, height);
                    if (clamped.X != pair.Value.X || clamped.Y != pair.Value.Y)
                    {
                        pair.Value.X = clamped.X;
                        pair.Value.Y = clamped.Y;
                        changed = true;
                    }
                }
                if (changed) MarkDirty();
            }
            if (changed) RequestPanelSync();
        }

        // =============================================================
        // 自动定时（移植 useDesktopScan 自动扫描/收纳 effect）
        // =============================================================
        private void RestartAutoTimer()
        {
            lock (stateLock)
            {
                DisposeTimer(ref autoTimer);
                if (disposed) return;
                if (!preferences.AutomaticScan && !preferences.AutomaticOrganize) return;
                double intervalMs = preferences.AutomaticOrganize
                    ? Math.Max(4000, Math.Min(12000, preferences.OrganizeDelaySeconds * 500))
                    : 30000;
                autoTimer = new Timer(OnAutoTimer, null, (int)intervalMs, (int)intervalMs);
            }
        }

        private void OnAutoTimer(object state)
        {
            host.RunOnUi(delegate
            {
                if (disposed) return;
                bool interactive;
                lock (stateLock) { interactive = interactionActive; }
                // 交互中跳过自动周期（对齐前端 interactionActive 判断）
                if (interactive) return;
                RunScan(true);   // 扫描完成回调里按策略自动收纳（MaybeAutoOrganize）
            });
        }

        // =============================================================
        // 门户目录列表
        // =============================================================
        private string[] GetPortalPaths()
        {
            List<string> paths = new List<string>();
            lock (stateLock)
            {
                foreach (CategoryData category in preferences.Categories)
                {
                    if (category.PortalPath != null && category.PortalPath.Trim().Length > 0) paths.Add(category.PortalPath);
                }
            }
            return paths.ToArray();
        }

        // =============================================================
        // 通知
        // =============================================================
        private void PostToast(string message, string type)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["message"] = message;
            payload["type"] = type;
            host.PostEvent("toast", payload);
        }
    }
}
