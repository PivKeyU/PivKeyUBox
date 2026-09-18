using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PivkeyOrganizer
{
    // 唤起/回落会话（按 DeskBox WidgetManager.ZOrder / TrayAnimation / WidgetSessionManager
    // 的操作逻辑移植）：分区常驻桌面层；托盘“显示全部分区”进入唤起会话——整组脱离桌面层、
    // 以“全员瞬态置顶 → 逆序清除”的原子序列浮到普通层级带顶部（非持久 TopMost，其他窗口
    // 激活时可正常盖过）；用户离开后由恢复监视器把整组一次性送回桌面层。
    //
    // 回落信号（DeskBox 的三信号体系，判定顺序固定）：
    //   1. own-foreground-leave  分区（或自家任何窗口）曾拿前台后离开 —— 最可靠主路径；
    //   2. foreground-changed    激活从未成功但前台窗口变化（≠唤起时刻前台）—— 激活失败的
    //                            主路径（SetForegroundWindow 受前台锁/UIPI 限制经常失败，属合法状态）；
    //   3. outside-click         50ms 高位 GetAsyncKeyState 鼠标边沿采样兜底——检测跨进程
    //                            点击只能用高位物理状态 + 自记 up→down 边沿（低位 & 0x0001 只对
    //                            本线程消息队列可靠，DeskBox 坑 #1），并在按下瞬间过滤光标位置。
    //
    // 防误触与防死锁：
    //   - 唤起后 160ms 抑制窗：覆盖唤起瞬间的激活/动画事件，防止刚浮起就回落；
    //   - 交互深度计数（拖动/缩放/菜单/输入）：深度 > 0 时监视器跳过；
    //   - 泄漏看门狗：交互深度 > 0 且自家无前台持续 10 秒 → 判定泄漏强制复位
    //     （DeskBox 架构文档规划的最后一道闸，防止 Begin/End 配对泄漏永久堵死回落）；
    //   - generation 代际：每次唤起/回落自增，过期的延迟回调捕获后比对作废。
    internal static class LayerSession
    {
        private const int RestoreMonitorIntervalMs = 200;   // 恢复监视器周期
        private const int MouseSamplerIntervalMs = 50;      // 鼠标采样周期（必须小于典型按下时长 50-150ms）
        private const int RaiseSuppressMs = 160;            // 唤起后回落抑制窗
        private const int InteractionLeakMs = 10 * 1000;    // 交互深度泄漏看门狗阈值
        private const uint GaRoot = 2;
        private const int VkLButton = 0x01;
        private const int VkRButton = 0x02;
        private const int VkMButton = 0x04;
        private const int VkXButton1 = 0x05;
        private const int VkXButton2 = 0x06;

        private static readonly object gate = new object();
        private static DispatcherTimer restoreTimer;
        private static DispatcherTimer mouseSampler;
        private static bool raised;
        private static bool toggling;
        private static IntPtr foregroundAtRaiseTime;
        private static DateTime suppressUntilUtc = DateTime.MinValue;
        private static int generation;
        private static readonly List<IntPtr> raisedHandles = new List<IntPtr>();
        private static readonly Dictionary<IntPtr, int> interactionDepths = new Dictionary<IntPtr, int>();
        private static int interactionDepth;
        private static DateTime interactionStartedUtc = DateTime.MinValue;
        private static bool hasOwnForegroundSinceRaise;
        private static bool outsideMousePressObserved;
        private static bool lastMouseButtonsDown;
        private static Action<bool> interactionChanged;     // → Manager.SetInteractionActive（暂停自动收纳）
        private static bool initialized;

        internal static bool IsRaised
        {
            get { lock (gate) return raised; }
        }

        internal static bool IsInteractionActive
        {
            get { lock (gate) return interactionDepth > 0; }
        }

        // UI 线程初始化一次（DesktopWindow 构造时调用）。
        internal static void Initialize(Action<bool> onInteractionChanged)
        {
            lock (gate)
            {
                if (initialized) return;
                initialized = true;
                interactionChanged = onInteractionChanged;
            }
        }

        // =============================================================
        // 唤起（DeskBox RaiseWidgetsFromTrayAsync 序列）
        // =============================================================

        // 进入/刷新唤起会话：handles 为已 Show 的分区窗口（调用方保证可见）。
        // 会话进行中再次调用只刷新组内成员并重放浮起脉冲，不重置回落判定基准。
        internal static void RaiseAll(List<IntPtr> handles)
        {
            List<IntPtr> live = new List<IntPtr>();
            foreach (IntPtr handle in handles)
            {
                if (handle != IntPtr.Zero && IsWindow(handle)) live.Add(handle);
            }
            if (live.Count == 0) return;
            bool wasRaised;
            lock (gate)
            {
                if (toggling) return;
                toggling = true;
                wasRaised = raised;
                generation++;
                if (!wasRaised)
                {
                    // 前台在展示“前”捕获：展示后再读会读到自家窗口（DeskBox 同款时序）
                    foregroundAtRaiseTime = GetForegroundWindow();
                    hasOwnForegroundSinceRaise = WidgetLayer.IsOwnWindow(foregroundAtRaiseTime);
                    suppressUntilUtc = DateTime.UtcNow.AddMilliseconds(RaiseSuppressMs);
                    outsideMousePressObserved = false;
                }
                raisedHandles.Clear();
                raisedHandles.AddRange(live);
                raised = true;
            }
            try
            {
                // 每窗脱离桌面层 + 瞬态脉冲（物理浮起），随后整组“全员置顶 → 逆序清除”
                // 的原子序列把组抬到普通带顶部且保持内部顺序（清除顺序决定组内次序）。
                foreach (IntPtr handle in live) WidgetLayer.RaiseTransient(handle);
                foreach (IntPtr handle in live) WidgetLayer.SetWindowTopMost(handle);
                for (int i = live.Count - 1; i >= 0; i--) WidgetLayer.ClearWindowTopMost(live[i]);
                StartTimers();
                // 最后一个分区尝试拿前台；受前台锁/UIPI 限制可能失败——失败合法，
                // 回落依赖“前台变化 + 鼠标边沿”两条信号（DeskBox 坑 #3）。
                SetForegroundWindow(live[live.Count - 1]);
                DesktopWindow.Log("[LayerSession] raise generation=" + generation + " count=" + live.Count + " refreshed=" + (wasRaised ? "1" : "0"));
            }
            finally
            {
                lock (gate) toggling = false;
            }
        }

        // 结束唤起会话（托盘“隐藏全部分区”入口）：只做组恢复（送回桌面层），
        // 窗口隐藏由宿主用 WPF Hide() 完成——原生 ShowWindow 会与 WPF 可见性状态失步。
        internal static void HideAll(List<IntPtr> handles)
        {
            Restore("tray-hide");
        }

        // =============================================================
        // 恢复监视器（200ms）+ 鼠标边沿采样器（50ms）
        // =============================================================

        private static void StartTimers()
        {
            RunOnUi(delegate
            {
                lock (gate)
                {
                    if (restoreTimer == null)
                    {
                        restoreTimer = new DispatcherTimer(DispatcherPriority.Background);
                        restoreTimer.Interval = TimeSpan.FromMilliseconds(RestoreMonitorIntervalMs);
                        restoreTimer.Tick += delegate { RestoreMonitorTick(); };
                    }
                    if (mouseSampler == null)
                    {
                        mouseSampler = new DispatcherTimer(DispatcherPriority.Background);
                        mouseSampler.Interval = TimeSpan.FromMilliseconds(MouseSamplerIntervalMs);
                        mouseSampler.Tick += delegate { MouseSamplerTick(); };
                    }
                    // 预充当前按键状态：防止唤起动作期间按住的那次点击被误判为新按下
                    lastMouseButtonsDown = IsAnyMouseButtonDown();
                    outsideMousePressObserved = false;
                    restoreTimer.Start();
                    mouseSampler.Start();
                }
            });
        }

        private static void StopTimers()
        {
            RunOnUi(delegate
            {
                lock (gate)
                {
                    if (restoreTimer != null) restoreTimer.Stop();
                    if (mouseSampler != null) mouseSampler.Stop();
                }
            });
        }

        private static void RunOnUi(Action action)
        {
            Dispatcher dispatcher = System.Windows.Application.Current != null ? System.Windows.Application.Current.Dispatcher : null;
            if (dispatcher == null) return;
            try { dispatcher.BeginInvoke(action); } catch { }
        }

        private static void RestoreMonitorTick()
        {
            bool stopped = false;
            lock (gate)
            {
                if (!raised) stopped = true;
                else if (toggling || DateTime.UtcNow < suppressUntilUtc) return;   // 忙碌 / 唤起抑制窗内
                else if (interactionDepth > 0)
                {
                    TryRecoverLeakedInteractionLocked();
                    return;
                }
            }
            if (stopped)
            {
                StopTimers();
                return;
            }

            // 前台判定在锁外做（Win32 调用不持锁）；写回状态时重新校验。
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return;   // 前台为空：owned 弹窗交接中，本 tick 不动
            IntPtr root = WidgetLayer.GetForegroundRoot();
            if (WidgetLayer.IsOwnWindow(root))
            {
                lock (gate) { if (raised) hasOwnForegroundSinceRaise = true; }   // 自家前台：保持唤起
                return;
            }
            if (WidgetLayer.IsTaskbarWindow(root)) return;       // 任务栏前台：视同桌面，保持唤起

            string reason = null;
            lock (gate)
            {
                if (!raised || toggling || DateTime.UtcNow < suppressUntilUtc || interactionDepth > 0) return;
                if (!hasOwnForegroundSinceRaise)
                {
                    if (foreground != foregroundAtRaiseTime)
                    {
                        reason = "foreground-changed";           // 激活失败时的主路径
                    }
                    else if (outsideMousePressObserved)
                    {
                        outsideMousePressObserved = false;       // 激活失败且点了同一窗口的兜底
                        reason = "outside-click";
                    }
                }
                else
                {
                    reason = "own-foreground-leave";             // 曾拿前台后离开：最可靠主路径
                }
                if (reason != null) generation++;
            }
            if (reason != null) Restore(reason);
        }

        private static void MouseSamplerTick()
        {
            bool stop = false;
            lock (gate)
            {
                if (!raised) stop = true;
            }
            if (stop)
            {
                StopTimers();
                return;
            }

            bool isDown = IsAnyMouseButtonDown();
            if (isDown && !lastMouseButtonsDown)
            {
                // up→down 边沿：按下瞬间光标不在自家窗口/任务栏上 → 外部点击，
                // 置位交给 200ms 监视器消费（采样与消费分离，DeskBox 方案 B）。
                WidgetLayer.CursorPoint cursor;
                if (GetCursorPos(out cursor))
                {
                    IntPtr window = WindowFromPoint(new NativePoint { X = cursor.X, Y = cursor.Y });
                    IntPtr root = GetAncestor(window, GaRoot);
                    if (root == IntPtr.Zero) root = window;
                    if (!WidgetLayer.IsOwnWindow(root) && !WidgetLayer.IsTaskbarWindow(root))
                    {
                        lock (gate) outsideMousePressObserved = true;
                    }
                }
            }
            lastMouseButtonsDown = isDown;
        }

        // =============================================================
        // 回落
        // =============================================================

        // 整组回落：清置顶 → 重挂桌面层 → 组内相对顺序整理（RestoreGroupToDesktopLayer）。
        // 单窗只负责清理自身状态；全局落点由这一处按组确定（DeskBox 关键约束）。
        private static void Restore(string reason)
        {
            List<IntPtr> live = new List<IntPtr>();
            lock (gate)
            {
                if (!raised || toggling) return;
                raised = false;
                toggling = true;
                generation++;
                foreach (IntPtr handle in raisedHandles)
                {
                    if (handle != IntPtr.Zero && IsWindow(handle) && IsWindowVisible(handle)) live.Add(handle);
                }
                raisedHandles.Clear();
            }
            try
            {
                WidgetLayer.RestoreGroupToDesktopLayer(live);
                DesktopWindow.Log("[LayerSession] restored reason=" + reason + " count=" + live.Count);
            }
            finally
            {
                lock (gate) toggling = false;
            }
            StopTimers();
        }

        // =============================================================
        // 交互深度（Begin/End 配对）+ 泄漏看门狗
        // =============================================================

        internal static void BeginInteraction(IntPtr hwnd)
        {
            bool becameActive = false;
            lock (gate)
            {
                int depth;
                interactionDepths.TryGetValue(hwnd, out depth);
                interactionDepths[hwnd] = depth + 1;
                interactionDepth++;
                if (interactionDepth == 1)
                {
                    interactionStartedUtc = DateTime.UtcNow;
                    becameActive = true;
                }
            }
            if (becameActive && interactionChanged != null) interactionChanged(true);
        }

        internal static void EndInteraction(IntPtr hwnd)
        {
            bool becameInactive = false;
            lock (gate)
            {
                int depth;
                if (interactionDepths.TryGetValue(hwnd, out depth) && depth > 0) interactionDepths[hwnd] = depth - 1;
                if (interactionDepth > 0) interactionDepth--;
                if (interactionDepth == 0) becameInactive = true;
            }
            if (becameInactive && interactionChanged != null) interactionChanged(false);
        }

        // 看门狗：交互深度 > 0 且自家无前台持续超过阈值 → 判定泄漏（Begin/End 配对丢失、
        // 捕获被系统抢走等），强制复位。真实交互必有自家前台，不会误伤。
        private static void TryRecoverLeakedInteractionLocked()
        {
            if (interactionDepth <= 0) return;
            if (DateTime.UtcNow - interactionStartedUtc < TimeSpan.FromMilliseconds(InteractionLeakMs)) return;
            IntPtr root = WidgetLayer.GetForegroundRoot();
            if (WidgetLayer.IsOwnWindow(root)) return;
            interactionDepths.Clear();
            interactionDepth = 0;
            DesktopWindow.Log("[LayerSession] 交互深度泄漏（超时无自家前台），已强制复位");
        }

        internal static void OnWindowClosed(IntPtr hwnd)
        {
            lock (gate)
            {
                int depth;
                if (interactionDepths.TryGetValue(hwnd, out depth) && depth > 0)
                {
                    interactionDepth -= depth;
                    interactionDepths.Remove(hwnd);
                    if (interactionDepth < 0) interactionDepth = 0;
                }
                raisedHandles.Remove(hwnd);
            }
        }

        // =============================================================
        // Win32
        // =============================================================

        // 高位（& 0x8000）= 全局物理按下状态，与目标进程是否提权无关（DeskBox 坑 #1）。
        private static bool IsAnyMouseButtonDown()
        {
            return (GetAsyncKeyState(VkLButton) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VkRButton) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VkMButton) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VkXButton1) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VkXButton2) & 0x8000) != 0;
        }

        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out WidgetLayer.CursorPoint point);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }
    }
}
