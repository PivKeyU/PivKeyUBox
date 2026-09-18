using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PivkeyOrganizer.ShellMenu
{
    // 原生右键菜单 helper（独立进程）：ShellMenu.exe <文件路径>
    // 在鼠标位置弹出真正的 Windows 资源管理器右键菜单，
    // 用户选中某一项后执行该命令并退出。
    // 采用经典的 "Shell Context Menu in C#" 模式（codeproject），
    // 崩溃只会影响本 helper 进程，不会波及主程序。
    // 注意：TrackPopupMenuEx 在没有任何窗口与消息循环的进程中会立即失败，
    // 因此这里用一个不可见的宿主窗口承载消息循环后再弹出菜单。
    internal static class Program
    {
        // TrackPopupMenuEx 标志
        private const uint TpmReturnCmd = 0x0100;   // 返回选中项的命令 ID
        private const uint TpmRightButton = 0x0002; // 右键也可选中
        private const uint TpmNonNotify = 0x0080;   // 不向宿主发送菜单通知

        private const uint CmfNormal = 0x00000000;
        private const uint CmdFirst = 1;            // 首个命令 ID
        private const uint CmdLast = 0x7FFF;        // 末个命令 ID
        private const int SwShowNormal = 1;

        // IID_IContextMenu
        private static readonly Guid IidIContextMenu = new Guid("000214e4-0000-0000-c000-000000000046");

        private static int exitCode = 1;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args == null || args.Length < 1 || String.IsNullOrWhiteSpace(args[0])) return 1;
            try
            {
                // 启用视觉样式，让 TrackPopupMenuEx 弹出的系统菜单保持现代外观。
                Application.EnableVisualStyles();
                Application.Run(new MenuHost(args[0]));
                return exitCode;
            }
            catch
            {
                return 1;
            }
        }

        // 不可见的宿主窗口：提供消息循环与菜单 owner 窗口。
        private sealed class MenuHost : Form
        {
            private readonly string targetPath;

            public MenuHost(string path)
            {
                targetPath = path;
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.None;
                Opacity = 0;                                   // 完全不可见
                StartPosition = FormStartPosition.Manual;
                Location = new System.Drawing.Point(-32000, -32000); // 放到屏幕外，避免闪烁
                Size = new Size(1, 1);
                Shown += OnShown;
            }

            private void OnShown(object sender, EventArgs args)
            {
                try
                {
                    IntPtr desktopFolder;
                    if (SHGetDesktopFolder(out desktopFolder) != 0 || desktopFolder == IntPtr.Zero) return;
                    try
                    {
                        IntPtr pidl;
                        uint attributes;
                        if (SHParseDisplayName(targetPath, IntPtr.Zero, out pidl, 0, out attributes) != 0 || pidl == IntPtr.Zero) return;
                        try
                        {
                            // IShellFolder::GetUIObjectOf 位于 vtable 第 10 槽
                            // （前 3 槽为 IUnknown，其后为 ParseDisplayName..GetAttributesOf 共 7 个）。
                            GetUIObjectOfDelegate getUIObjectOf = GetVtableDelegate<GetUIObjectOfDelegate>(desktopFolder, 10);
                            IntPtr[] pidls = new IntPtr[] { pidl };
                            IntPtr contextMenu;
                            Guid iid = IidIContextMenu;
                            if (getUIObjectOf(desktopFolder, IntPtr.Zero, 1, pidls, ref iid, 0, out contextMenu) != 0 || contextMenu == IntPtr.Zero) return;
                            try
                            {
                                exitCode = ShowContextMenu(contextMenu);
                            }
                            finally { Marshal.Release(contextMenu); }
                        }
                        finally { CoTaskMemFree(pidl); }
                    }
                    finally { Marshal.Release(desktopFolder); }
                }
                catch
                {
                    exitCode = 1;
                }
                finally
                {
                    Close();
                }
            }

            // 建立菜单 -> 在光标处弹出 -> 有选中项则执行该命令。
            private int ShowContextMenu(IntPtr contextMenu)
            {
                // IContextMenu vtable：0-2 为 IUnknown，3=QueryContextMenu，4=InvokeCommand，5=GetCommandString。
                QueryContextMenuDelegate queryContextMenu = GetVtableDelegate<QueryContextMenuDelegate>(contextMenu, 3);
                InvokeCommandDelegate invokeCommand = GetVtableDelegate<InvokeCommandDelegate>(contextMenu, 4);

                IntPtr menu = CreatePopupMenu();
                if (menu == IntPtr.Zero) return 1;
                try
                {
                    queryContextMenu(contextMenu, menu, 0, CmdFirst, CmdLast, CmfNormal);

                    Point point;
                    GetCursorPos(out point);
                    int command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmNonNotify, point.X, point.Y, Handle, IntPtr.Zero);

                    if (command > 0)
                    {
                        // lpVerb 为整数时，低 16 位是相对 idCmdFirst 的命令偏移。
                        CMInvokeCommandInfoEx info = new CMInvokeCommandInfoEx();
                        info.CbSize = Marshal.SizeOf(typeof(CMInvokeCommandInfoEx));
                        info.FMask = 0;
                        info.Hwnd = IntPtr.Zero;
                        info.LpVerb = new IntPtr((long)(command - (int)CmdFirst));
                        info.NShow = SwShowNormal;
                        invokeCommand(contextMenu, ref info);
                        // 部分 shell 命令（如资源管理器扩展）在 InvokeCommand 返回后才完成启动，
                        // 稍作停留再退出，避免命令被打断。
                        System.Threading.Thread.Sleep(300);
                    }
                    return 0;
                }
                finally { DestroyMenu(menu); }
            }

            // 从 COM 对象的 vtable 指定槽位取出函数指针并转成委托。
            private static T GetVtableDelegate<T>(IntPtr comObject, int slot) where T : class
            {
                IntPtr vtable = Marshal.ReadIntPtr(comObject);
                IntPtr function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
                return (T)(object)Marshal.GetDelegateForFunctionPointer(function, typeof(T));
            }

            // COM 方法委托：第一个参数为 this 指针。
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int GetUIObjectOfDelegate(IntPtr thisPtr, IntPtr hwndOwner, int cidl, IntPtr[] apidl, ref Guid riid, int rgfReserved, out IntPtr ppvOut);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int QueryContextMenuDelegate(IntPtr thisPtr, IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int InvokeCommandDelegate(IntPtr thisPtr, ref CMInvokeCommandInfoEx info);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct CMInvokeCommandInfoEx
            {
                public int CbSize;
                public uint FMask;
                public IntPtr Hwnd;
                public IntPtr LpVerb;
                [MarshalAs(UnmanagedType.LPWStr)] public string LpParameters;
                [MarshalAs(UnmanagedType.LPWStr)] public string LpDirectory;
                public int NShow;
                public int DwHotKey;
                public IntPtr HIcon;
                [MarshalAs(UnmanagedType.LPWStr)] public string LpTitle;
                public IntPtr LpVerbW;
                [MarshalAs(UnmanagedType.LPWStr)] public string LpParametersW;
                [MarshalAs(UnmanagedType.LPWStr)] public string LpDirectoryW;
                [MarshalAs(UnmanagedType.LPWStr)] public string LpTitleW;
                public int PtInvokeX;
                public int PtInvokeY;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct Point { public int X; public int Y; }

            [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IntPtr desktopFolder);
            [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr binder, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);
            [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr memory);
            [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
            [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
            [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
            [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr trackData);
        }
    }
}
