using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PivkeyOrganizer
{
    // 窗口层级服务（按 DeskBox WidgetLayerService / Win32Helper / RelativeLayerRestorePolicy
    // 的操作逻辑重构，替换旧 DesktopLayer）：
    //
    // 桌面层挂载（分区静置态）：
    //   - owner 指向 Explorer 现存 SHELLDLL_DefView（桌面图标视图），分区恰好位于桌面图标
    //     之上、普通窗口之下，Win+D/最小化不会收走；
    //   - SetWindowLongPtr 必须回读验证：owner 写入可能静默失败，失败时还原原 owner、
    //     判定缓存失效并报告降级（调用方继续走 Progman/自动升级路径）；
    //   - DefView 不存在（开机桌面未就绪）时回退挂 Progman 并登记，由后台定时器在桌面
    //     就绪后自动升级；绝不主动创建 WorkerW —— 避免登录期与 Explorer 图标布局恢复
    //     竞争打乱用户桌面图标（与 DeskBox 相同的取舍）；
    //   - 首次挂载前记录原 owner，供唤起会话脱离桌面层时还原。
    //
    // 层级原语（唤起不靠持久置顶）：
    //   - BringWindowTemporarilyToFront：TOPMOST→NOTOPMOST 同 flags 脉冲，窗口浮到普通
    //     层级带顶部但不占用 WS_EX_TOPMOST，其他窗口激活时可正常盖过；
    //   - RestoreGroupToDesktopLayer：整组清置顶 → 重挂桌面层 → 组内压底，再以
    //     BeginDeferWindowPos 链式排列整理组内相对顺序（失败逐窗 SetWindowPos 兜底）；
    //   - 空闲整理（peer-only）只调整分区之间的顺序，边界取“当前最高分区的上一窗口”，
    //     绝不把整组绝对置底破坏“前台窗口 > 分区组”的相对层级（DeskBox 坑 #2）。
    //
    // 前台判定：
    //   - GetForegroundRoot 取 GA_ROOTOWNER 所有权根，前台应用的可弹窗不会被误判成
    //     “外部应用与分区交错”；
    //   - IsDesktopShellWindow：Progman/WorkerW/SHELLDLL_DefView 祖先链；
    //   - IsTaskbarWindow：Shell_TrayWnd/Shell_SecondaryTrayWnd/NotifyIconOverflowWindow
    //     祖先链（任务栏在前台视同桌面壳，点击分区不抑制激活）；
    //   - IsOwnWindow：按进程号判定（范围宽：托盘/设置/搜索等自有窗口都算自家前台）。
    internal static class WidgetLayer
    {
        private const int GwlHwndParent = -8;
        private const uint GwHwndFirst = 0;
        private const uint GwHwndNext = 2;
        private const uint GwHwndPrev = 3;
        private const uint GaRootOwner = 3;
        internal static readonly IntPtr HwndTop = new IntPtr(0);
        internal static readonly IntPtr HwndBottom = new IntPtr(1);
        internal static readonly IntPtr HwndTopMost = new IntPtr(-1);
        internal static readonly IntPtr HwndNoTopMost = new IntPtr(-2);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpShowWindow = 0x0040;

        private static readonly object gate = new object();
        private static readonly object orderGate = new object();
        private static IntPtr cachedIconView;
        private static readonly Dictionary<IntPtr, IntPtr> originalOwners = new Dictionary<IntPtr, IntPtr>();
        private static readonly List<IntPtr> fallbackWindows = new List<IntPtr>();
        private static Timer upgradeTimer;
        private static int upgradeTicks;
        // P/Invoke 失败绝不能在类型初始化器里抛（否则整个应用启动即崩），这里兜底为 0
        private static readonly uint currentProcessId = LoadCurrentProcessId();

        // =============================================================
        // 桌面层挂载
        // =============================================================

        // 把窗口挂到桌面层（带回读验证）；返回 true 表示挂到了 SHELLDLL_DefView（最优层），
        // false 表示回退到 Progman 或桌面窗口尚未就绪（已登记等待自动升级）。
        internal static bool Attach(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            RecordOriginalOwner(hwnd);
            IntPtr iconView = FindDesktopIconView();
            if (iconView != IntPtr.Zero && TrySetOwnerVerified(hwnd, iconView))
            {
                lock (gate) fallbackWindows.Remove(hwnd);
                return true;
            }
            // 降级：挂 Progman（同样验证；失败也不阻塞——窗口仍可用，等待自动升级）
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero) TrySetOwnerVerified(hwnd, progman);
            RegisterFallback(hwnd);
            return false;
        }

        private static void RecordOriginalOwner(IntPtr hwnd)
        {
            lock (gate)
            {
                if (originalOwners.ContainsKey(hwnd)) return;
                originalOwners[hwnd] = GetWindowLongPtr(hwnd, GwlHwndParent);
            }
        }

        // 设置 owner 并回读验证；失败时清理状态并返回 false（不改异常路径，调用方降级）。
        private static bool TrySetOwnerVerified(IntPtr hwnd, IntPtr owner)
        {
            if (GetWindowLongPtr(hwnd, GwlHwndParent) == owner) return true;
            SetLastError(0);
            SetWindowLongPtr(hwnd, GwlHwndParent, owner);
            if (GetWindowLongPtr(hwnd, GwlHwndParent) != owner)
            {
                // owner 写入静默失败：若目标是缓存的 DefView，多半是 Explorer 重启后缓存失效
                if (owner == FindCachedIconViewUnverified())
                {
                    InvalidateDesktopIconViewCache();
                    Log("[WidgetLayer] owner 写入回读失败，已失效 DefView 缓存 hwnd=" + hwnd);
                }
                return false;
            }
            return true;
        }

        private static IntPtr FindCachedIconViewUnverified()
        {
            lock (gate) return cachedIconView;
        }

        internal static void InvalidateDesktopIconViewCache()
        {
            lock (gate) cachedIconView = IntPtr.Zero;
        }

        internal static void RegisterFallback(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            lock (gate)
            {
                if (!fallbackWindows.Contains(hwnd)) fallbackWindows.Add(hwnd);
                if (upgradeTimer == null)
                {
                    upgradeTicks = 0;
                    upgradeTimer = new Timer(delegate { TryUpgradeFallbacks(); }, null, 2000, 2000);
                }
            }
        }

        // 桌面就绪后把回退窗口升级挂到 SHELLDLL_DefView；约 60 秒后放弃，避免常驻线程。
        private static void TryUpgradeFallbacks()
        {
            List<IntPtr> snapshot;
            lock (gate)
            {
                upgradeTicks++;
                if (upgradeTicks > 30)
                {
                    if (upgradeTimer != null) upgradeTimer.Dispose();
                    upgradeTimer = null;
                    return;
                }
                if (fallbackWindows.Count == 0) return;
                snapshot = new List<IntPtr>(fallbackWindows);
            }
            IntPtr iconView = FindDesktopIconView();
            if (iconView == IntPtr.Zero) return;
            foreach (IntPtr hwnd in snapshot)
            {
                lock (gate) fallbackWindows.Remove(hwnd);
                if (!IsWindow(hwnd)) continue;
                if (TrySetOwnerVerified(hwnd, iconView)) continue;
                lock (gate) if (!fallbackWindows.Contains(hwnd)) fallbackWindows.Add(hwnd);
            }
        }

        // 查找桌面图标视图（SHELLDLL_DefView），带句柄缓存；Explorer 重启后句柄失效时
        // 自动重新枚举。只在已存在的顶层窗口里找子窗口，不触碰 WorkerW。
        internal static IntPtr FindDesktopIconView()
        {
            lock (gate)
            {
                if (cachedIconView != IntPtr.Zero && IsWindow(cachedIconView)) return cachedIconView;
                cachedIconView = IntPtr.Zero;
            }
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr topLevel, IntPtr lParam)
            {
                IntPtr child = FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (child != IntPtr.Zero)
                {
                    found = child;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                lock (gate) cachedIconView = found;
            }
            return found;
        }

        // =============================================================
        // 层级原语
        // =============================================================

        // 瞬态置顶脉冲：先 TOPMOST 再 NOTOPMOST（同一 flags、同步调用），窗口浮到普通
        // 层级带顶部但不占用 WS_EX_TOPMOST。调用返回后窗口不是 TopMost。
        internal static void BringWindowTemporarilyToFront(IntPtr hwnd)
        {
            const uint flags = SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow;
            SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, flags);
            SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, flags);
        }

        internal static void SetWindowTopMost(IntPtr hwnd)
        {
            SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }

        internal static void ClearWindowTopMost(IntPtr hwnd)
        {
            SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }

        internal static bool IsWindowTopMost(IntPtr hwnd)
        {
            return (GetWindowLong(hwnd, GwlExStyle) & WsExTopMost) != 0;
        }

        // 唤起：脱离桌面层（owner 还原为挂载前值）+ 瞬态置顶脉冲，浮到普通层级带顶部。
        internal static void RaiseTransient(IntPtr hwnd)
        {
            IntPtr original;
            bool had;
            lock (gate) had = originalOwners.TryGetValue(hwnd, out original);
            if (had) SetWindowLongPtr(hwnd, GwlHwndParent, original);
            BringWindowTemporarilyToFront(hwnd);
        }

        // 脱离桌面层（owner 还原为挂载前值，清除回退队列），供便签置顶（Pinned）时使用
        internal static void Detach(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr original = IntPtr.Zero;
            lock (gate)
            {
                originalOwners.TryGetValue(hwnd, out original);
                fallbackWindows.Remove(hwnd);
            }
            SetWindowLongPtr(hwnd, GwlHwndParent, original);
        }

        // 回落到桌面层：清置顶 → 重新挂 DefView（验证失败自动降级登记）→ 组内压底。
        // 整组的最终相对顺序由 RestoreGroupToDesktopLayer 统一整理。
        internal static void RestoreToDesktop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            ClearWindowTopMost(hwnd);
            Attach(hwnd);
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);
        }

        // 整组回落：逐窗 RestoreToDesktop 后用一次 DeferWindowPos 事务建立
        // “桌面图标之上、组内按传入顺序”的连续链，失败再逐窗兜底。
        internal static void RestoreGroupToDesktopLayer(IReadOnlyList<IntPtr> handles)
        {
            if (handles == null || handles.Count == 0) return;
            List<IntPtr> live = new List<IntPtr>();
            foreach (IntPtr handle in handles)
            {
                if (handle == IntPtr.Zero || !IsWindow(handle)) continue;
                RestoreToDesktop(handle);
                live.Add(handle);
            }
            ApplyPeerOrderHighestToLowest(live);
        }

        // 空闲整理（peer-only）：以当前最高分区的“上一窗口”为边界整理组内顺序。
        // 边界为空说明整组已在最底，无需整理——绝不额外调用 HWND_BOTTOM。
        internal static void ApplyPeerOrderHighestToLowest(IReadOnlyList<IntPtr> handles)
        {
            if (handles == null || handles.Count == 0) return;
            List<IntPtr> live = new List<IntPtr>();
            foreach (IntPtr handle in handles)
            {
                if (handle != IntPtr.Zero && IsWindow(handle)) live.Add(handle);
            }
            if (live.Count == 0) return;
            IntPtr highest = FindHighestPeer(live);
            if (highest == IntPtr.Zero) return;
            IntPtr boundary = GetWindow(highest, GwHwndPrev);
            if (boundary == IntPtr.Zero) return;   // 组已在最底：无需整理
            ApplyWindowOrderHighestToLowest(live, boundary);
        }

        // DeferWindowPos 链式排列：第一个窗口插到 boundary 之后，之后每个窗口插到
        // 前一个窗口之后，一次事务建立“boundary > h0 > h1 > …”的连续链；
        // 任何一步失败退化为同边界、同顺序的逐窗 SetWindowPos（DeskBox 兜底路径）。
        private static bool ApplyWindowOrderHighestToLowest(IReadOnlyList<IntPtr> handles, IntPtr boundary)
        {
            if (handles.Count == 0) return true;
            lock (orderGate)
            {
                const uint flags = SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder;
                IntPtr insertAfter = boundary;
                IntPtr deferred = BeginDeferWindowPos(handles.Count);
                if (deferred != IntPtr.Zero)
                {
                    foreach (IntPtr handle in handles)
                    {
                        deferred = DeferWindowPos(deferred, handle, insertAfter, 0, 0, 0, 0, flags);
                        if (deferred == IntPtr.Zero) break;
                        insertAfter = handle;
                    }
                    if (deferred != IntPtr.Zero && EndDeferWindowPos(deferred)) return true;
                }
                insertAfter = boundary;
                bool succeeded = true;
                foreach (IntPtr handle in handles)
                {
                    succeeded &= SetWindowPos(handle, insertAfter, 0, 0, 0, 0, flags);
                    insertAfter = handle;
                }
                return succeeded;
            }
        }

        // 从 Z 顶沿 GW_HWNDNEXT 找第一个属于本组的窗口。
        private static IntPtr FindHighestPeer(List<IntPtr> handles)
        {
            IntPtr first = GetWindow(handles[0], GwHwndFirst);
            IntPtr current = first;
            int guard = 0;
            while (current != IntPtr.Zero && guard++ < 4096)
            {
                foreach (IntPtr handle in handles)
                {
                    if (handle == current) return handle;
                }
                current = GetWindow(current, GwHwndNext);
            }
            return IntPtr.Zero;
        }

        // =============================================================
        // 陈旧状态修复（DeskBox RestoreDesktopPinnedBottomState 语义）
        // =============================================================

        // 点击抑制路径顺手修复陈旧挂载/Z 状态（无闪烁，全部 SWP_NOACTIVATE）：
        // Explorer 重启后 owner 句柄失效、或分区被意外置顶/抬升时，静默修回
        // “桌面层 + 组内底部”。仅桌面静置态调用（唤起会话中禁止）。
        internal static void RepairDesktopBottomState(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            bool topMost = IsWindowTopMost(hwnd);
            IntPtr iconView = FindDesktopIconView();
            IntPtr owner = GetWindowLongPtr(hwnd, GwlHwndParent);
            // 仅在 DefView 可用但 owner 不一致时视为陈旧（DefView 缺失=正常降级期，
            // 由 Attach 的自动升级处理，不能每次点击都当作修复）
            bool ownerStale = iconView != IntPtr.Zero && owner != iconView;
            if (!topMost && !ownerStale) return;
            if (topMost) ClearWindowTopMost(hwnd);
            if (ownerStale) Attach(hwnd);
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);
            Log("[WidgetLayer] 已修复陈旧桌面层状态 hwnd=" + hwnd + (ownerStale ? " owner" : "") + (topMost ? (ownerStale ? "+topmost" : " topmost") : ""));
        }

        // =============================================================
        // 前台判定
        // =============================================================

        // 前台所有权根（GA_ROOTOWNER）：前台应用弹出的对话框/气泡不会被误判成交错窗口。
        internal static IntPtr GetForegroundRoot()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = GetAncestor(foreground, GaRootOwner);
            return root != IntPtr.Zero ? root : foreground;
        }

        // 点击分区是否抑制窗口激活（DeskBox 指针激活策略）：
        // 无前台（过渡中）、自家窗口、桌面壳、任务栏在前台 → 放行；
        // 外部应用前台 → 抑制（点击不抢焦点、不把分区抬到应用之上）。
        internal static bool ShouldSuppressPointerActivation()
        {
            IntPtr root = GetForegroundRoot();
            if (root == IntPtr.Zero) return false;                 // 前台交接中：放行
            if (IsOwnWindow(root)) return false;                  // 自家窗口间切换：正常激活
            if (IsDesktopShellWindow(root)) return false;         // 前台是桌面：正常激活
            if (IsTaskbarWindow(root)) return false;              // 前台是任务栏：视同桌面
            return true;
        }

        internal static bool IsDesktopShellWindow(IntPtr hwnd)
        {
            return WindowOrAncestorHasClass(hwnd, delegate(string className)
            {
                return className == "Progman" || className == "WorkerW" || className == "SHELLDLL_DefView";
            });
        }

        internal static bool IsTaskbarWindow(IntPtr hwnd)
        {
            return WindowOrAncestorHasClass(hwnd, delegate(string className)
            {
                return className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd" || className == "NotifyIconOverflowWindow";
            });
        }

        // 沿 GetParent 祖先链匹配类名，再查 GA_ROOT 兜底（对齐 DeskBox 的判定实现）。
        private static bool WindowOrAncestorHasClass(IntPtr hwnd, Predicate<string> match)
        {
            if (hwnd == IntPtr.Zero) return false;
            IntPtr current = hwnd;
            while (current != IntPtr.Zero)
            {
                if (WindowHasClass(current, match)) return true;
                current = GetParent(current);
            }
            IntPtr root = GetAncestor(hwnd, 2 /* GA_ROOT */);
            return root != IntPtr.Zero && WindowHasClass(root, match);
        }

        private static bool WindowHasClass(IntPtr hwnd, Predicate<string> match)
        {
            StringBuilder name = new StringBuilder(64);
            if (GetClassName(hwnd, name, 64) <= 0) return false;
            return match(name.ToString());
        }

        // 本进程窗口（面板/托盘/设置/搜索）：范围宽，自家任何窗口拿到前台都算“还在用分区”。
        internal static bool IsOwnWindow(IntPtr hwnd)
        {
            if (currentProcessId == 0) return false;
            if (hwnd == IntPtr.Zero) return false;
            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            return processId == currentProcessId;
        }

        private static void Log(string message)
        {
            DesktopWindow.Log(message);
        }

        private static uint LoadCurrentProcessId()
        {
            try { return GetCurrentProcessId(); }
            catch { return 0; }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint errorCode);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr afterChild, string className, string windowName);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr BeginDeferWindowPos(int count);
        [DllImport("user32.dll")] private static extern IntPtr DeferWindowPos(IntPtr info, IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(IntPtr info);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

        internal struct CursorPoint { public int X; public int Y; }

        private const int GwlExStyle = -20;
        private const int WsExTopMost = 0x00000008;
    }
}
