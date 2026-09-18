using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace PivkeyOrganizer
{
    // 原生文件启动（按 DeskBox ExplorerShellLaunchService 的操作逻辑）：
    //
    //  1. 主路径：借 Explorer 桌面窗口宿主的 Shell 对象在 Explorer 进程内 ShellExecute——
    //     子进程继承 Explorer 的用户环境（避免继承本进程的环境变量/完整性级别），并先
    //     CoAllowSetForegroundWindow 把前台权转移给 Explorer 侧 Shell，让新启动的应用
    //     可以正常拿到前台；
    //  2. 回退：本进程 ShellExecute（Process.Start UseShellExecute=true）；
    //  3. 无关联程序（ERROR_NO_ASSOCIATION）时弹系统“打开方式”对话框（SHOpenWithDialog）。
    internal static class ShellLauncher
    {
        private const int SwShowNormal = 1;
        private const int ErrorNoAssociation = 1155;
        private const uint OaifAllowRegistration = 0x1;
        private const uint OaifExec = 0x4;

        internal static void Open(string path)
        {
            try { DesktopWindow.Log("ShellLauncher 正在请求启动: " + path); } catch { }
            if (TryOpenViaExplorerHost(path))
            {
                try { DesktopWindow.Log("ShellLauncher 已通过 Explorer 宿主成功启动: " + path); } catch { }
                return;
            }
            try
            {
                try { DesktopWindow.Log("ShellLauncher 回退到 Process.Start 启动: " + path); } catch { }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                try { DesktopWindow.Log("ShellLauncher 启动异常: " + error.NativeErrorCode + " - " + error.Message); } catch { }
                if (error.NativeErrorCode == ErrorNoAssociation)
                {
                    ShowOpenWithDialog(path);
                    return;
                }
                throw;
            }
        }

        // 借 Explorer 桌面窗口的 Shell 对象启动；任何一步失败都返回 false 走回退。
        private static bool TryOpenViaExplorerHost(string path)
        {
            object shell = null;
            object windows = null;
            object desktopWindow = null;
            object document = null;
            object explorerShell = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;
                shell = Activator.CreateInstance(shellType);
                windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                if (windows == null) return false;
                // FindWindowSW(0, 0, SWC_DESKTOP=8, out hwnd, SWFO_NEEDDISPATCH=1)：桌面窗口宿主
                object[] findArgs = new object[] { 0, 0, 8, null, 1 };
                Type windowsType = windows.GetType();
                desktopWindow = windowsType.InvokeMember("FindWindowSW", System.Reflection.BindingFlags.InvokeMethod, null, windows, findArgs);
                if (desktopWindow == null) return false;
                document = desktopWindow.GetType().InvokeMember("Document", System.Reflection.BindingFlags.GetProperty, null, desktopWindow, null);
                if (document == null) return false;
                explorerShell = document.GetType().InvokeMember("Application", System.Reflection.BindingFlags.GetProperty, null, document, null);
                if (explorerShell == null) return false;
                AllowForegroundTransfer(explorerShell);
                string workingDirectory = "";
                try
                {
                    string candidate = Path.GetDirectoryName(path);
                    if (Directory.Exists(candidate)) workingDirectory = candidate;
                }
                catch { }
                explorerShell.GetType().InvokeMember("ShellExecute", System.Reflection.BindingFlags.InvokeMethod, null, explorerShell,
                    new object[] { path, "", workingDirectory, "", SwShowNormal });
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseIfComObject(explorerShell);
                ReleaseIfComObject(document);
                ReleaseIfComObject(desktopWindow);
                ReleaseIfComObject(windows);
                ReleaseIfComObject(shell);
            }
        }

        // 把前台权转交给 Explorer 侧 Shell：子进程由“收到最后一次输入”的进程代为启动，
        // 不转移的话新应用可能拿不到前台（Windows 前台锁规则）。
        private static void AllowForegroundTransfer(object explorerShell)
        {
            try
            {
                IntPtr unknown = Marshal.GetIUnknownForObject(explorerShell);
                try { CoAllowSetForegroundWindow(unknown, IntPtr.Zero); }
                finally { Marshal.Release(unknown); }
            }
            catch { }
        }

        private static void ShowOpenWithDialog(string path)
        {
            OpenAsInfo info = new OpenAsInfo();
            info.File = path;
            info.Class = null;
            info.Flags = OaifAllowRegistration | OaifExec;
            SHOpenWithDialog(IntPtr.Zero, ref info);
        }

        private static void ReleaseIfComObject(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenAsInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string File;
            [MarshalAs(UnmanagedType.LPWStr)] public string Class;
            public uint Flags;
        }

        [DllImport("ole32.dll")] private static extern int CoAllowSetForegroundWindow(IntPtr proxy, IntPtr window);
        [DllImport("shell32.dll", PreserveSig = true)]
        private static extern int SHOpenWithDialog(IntPtr parent, ref OpenAsInfo info);
    }
}
