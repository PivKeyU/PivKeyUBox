using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PivkeyOrganizer
{
    // 原生 Shell 文件操作桥（按 DeskBox FileService.ShellTransfer 的多引擎思路裁剪）：
    //
    //  - 交互导入（拖拽收纳/设置页移动）：IFileOperation（CLSID_FileOperation）在专用 STA
    //    线程执行，带系统进度窗口（可取消、可看速率）；同名冲突由调用方用 GetAvailablePath
    //    预解析目标名规避，操作标志只保留 NOCONFIRMMKDIR，不弹冲突对话框；
    //  - 后台整理/撤销（静默模式）：同卷走 File.Move / File.Copy（rename 语义，原子、快），
    //    遇到跨卷（ERROR_NOT_SAME_DEVICE）把该项退回 IFileOperation 静默批量
    //    （FOF_SILENT|FOF_NOCONFIRMATION|FOF_NOERRORUI）——.NET Framework 的 File.Move
    //    不支持跨卷，Shell 层可以；
    //  - 结果对账（DeskBox ReconcileShellTransferResults 语义）：Shell 漏报/中止时按文件
    //    系统事实补齐（move：源消失且目标存在；copy：目标存在）。
    //
    //  安全约定保持不变：只移动/复制文件，不删除文件。
    internal static class NativeFileOps
    {
        private const uint FofSilent = 0x0004;
        private const uint FofNoConfirmation = 0x0010;
        private const uint FofNoConfirmMkDir = 0x0200;
        private const uint FofNoErrorUi = 0x0400;
        private const int ErrorNotSameDevice = -2147024752;   // 0x80070011 HRESULT

        internal sealed class TransferRequest
        {
            public string Source;
            public string DestinationFolder;
            public string DestinationName;   // 已由调用方 GetAvailablePath 预解析的最终名
        }

        internal sealed class TransferResult
        {
            public string Source;
            public string Destination;
            public bool Completed;
            public bool Aborted;
            public string Error;
        }

        // 目标已存在时依次尝试 “名称 (1)”、“名称 (2)”……；reserved 集合防止批量内
        // 两个同名源预解析出同一目标（DeskBox GetAvailablePath 的 reservedPaths 语义）。
        internal static string GetAvailablePath(string destination, HashSet<string> reserved)
        {
            bool occupied = File.Exists(destination) || Directory.Exists(destination);
            if (!occupied && reserved != null && reserved.Contains(destination.ToLowerInvariant())) occupied = true;
            if (!occupied) return destination;
            string directory = Path.GetDirectoryName(destination);
            string name = Path.GetFileNameWithoutExtension(destination);
            string extension = Path.GetExtension(destination);
            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(directory, String.Format("{0} ({1}){2}", name, i, extension));
                occupied = File.Exists(candidate) || Directory.Exists(candidate) ||
                    (reserved != null && reserved.Contains(candidate.ToLowerInvariant()));
                if (!occupied) return candidate;
            }
            return destination;
        }

        // 批量移动（move=true）或复制；showProgress 控制是否显示系统进度窗口。
        // 返回与请求一一对应的结果列表，逐项错误记录在 Error 中（不抛异常），
        // 调用方按需聚合（与旧版逐项 try/catch 语义兼容）。
        internal static List<TransferResult> Transfer(IList<TransferRequest> requests, IntPtr ownerWindow, bool showProgress, bool move)
        {
            List<TransferResult> results = new List<TransferResult>(requests != null ? requests.Count : 0);
            if (requests == null || requests.Count == 0) return results;
            if (showProgress) return ShellTransfer(requests, ownerWindow, true, move);

            // 静默模式：先逐项托管执行（同卷 rename，快且原子），跨卷项收集后交给 Shell 静默批量。
            List<TransferRequest> deferred = new List<TransferRequest>();
            List<TransferResult> deferredResults = new List<TransferResult>();
            foreach (TransferRequest request in requests)
            {
                string destination = Path.Combine(request.DestinationFolder, request.DestinationName);
                TransferResult result = new TransferResult();
                result.Source = request.Source;
                result.Destination = destination;
                try
                {
                    ManagedTransfer(request.Source, destination, move);
                    result.Completed = true;
                }
                catch (Exception error)
                {
                    if (IsDifferentVolumeError(error))
                    {
                        deferred.Add(request);
                        deferredResults.Add(result);
                        continue;
                    }
                    result.Error = error.Message;
                }
                results.Add(result);
            }
            if (deferred.Count > 0)
            {
                List<TransferResult> shellResults = ShellTransfer(deferred, ownerWindow, false, move);
                for (int i = 0; i < shellResults.Count; i++) results.Add(shellResults[i]);
            }
            return results;
        }

        // 单项静默移动（organize / undo / redo 的 host.Move 语义）。
        internal static void MoveSingle(string source, string destination)
        {
            try
            {
                ManagedTransfer(source, destination, true);
            }
            catch (Exception error)
            {
                if (!IsDifferentVolumeError(error)) throw;
                TransferRequest request = new TransferRequest();
                request.Source = source;
                request.DestinationFolder = Path.GetDirectoryName(destination);
                request.DestinationName = Path.GetFileName(destination);
                List<TransferRequest> requests = new List<TransferRequest>();
                requests.Add(request);
                List<TransferResult> results = ShellTransfer(requests, IntPtr.Zero, false, true);
                if (results.Count != 1 || !results[0].Completed)
                    throw new IOException(results.Count == 1 && results[0].Error != null ? results[0].Error : "跨卷移动失败：" + error.Message);
            }
        }

        private static void ManagedTransfer(string source, string destination, bool move)
        {
            if (move)
            {
                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination);
            }
            else
            {
                if (Directory.Exists(source)) CopyDirectory(source, destination);
                else File.Copy(source, destination, false);
            }
        }

        private static bool IsDifferentVolumeError(Exception error)
        {
            IOException ioError = error as IOException;
            if (ioError == null) return false;
            return Marshal.GetHRForException(ioError) == ErrorNotSameDevice;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
            foreach (string directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        // =============================================================
        // IFileOperation（专用 STA 线程 + 结果对账）
        // =============================================================

        private static List<TransferResult> ShellTransfer(IList<TransferRequest> requests, IntPtr ownerWindow, bool showProgress, bool move)
        {
            List<TransferResult> results = null;
            Exception failure = null;
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try { results = RunShellTransfer(requests, ownerWindow, showProgress, move); }
                catch (Exception error) { failure = error; }
            }));
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (results != null) return results;
            // COM 路径整体失败（初始化/CoCreateInstance 异常）：逐项报错，保持“不抛异常”契约
            List<TransferResult> fallback = new List<TransferResult>(requests.Count);
            string message = failure != null ? failure.Message : "Shell 操作初始化失败";
            foreach (TransferRequest request in requests)
            {
                TransferResult result = new TransferResult();
                result.Source = request.Source;
                result.Destination = Path.Combine(request.DestinationFolder, request.DestinationName);
                result.Error = message;
                fallback.Add(result);
            }
            return fallback;
        }

        private static List<TransferResult> RunShellTransfer(IList<TransferRequest> requests, IntPtr ownerWindow, bool showProgress, bool move)
        {
            List<TransferResult> results = new List<TransferResult>(requests.Count);
            int initResult = CoInitializeEx(IntPtr.Zero, 0x2 /* COINIT_APARTMENTTHREADED */);
            bool initialized = initResult >= 0 || initResult == 1 /* S_FALSE 已初始化 */;
            try
            {
                IFileOperation operation = (IFileOperation)new FileOperationClass();
                List<IShellItem> liveItems = new List<IShellItem>();
                try
                {
                    uint flags = FofNoConfirmMkDir;
                    if (!showProgress) flags |= FofSilent | FofNoConfirmation | FofNoErrorUi;
                    operation.SetOperationFlags(flags);
                    operation.SetOwnerWindow(ownerWindow);
                    foreach (TransferRequest request in requests)
                    {
                        TransferResult result = new TransferResult();
                        result.Source = request.Source;
                        result.Destination = Path.Combine(request.DestinationFolder, request.DestinationName);
                        results.Add(result);
                        IShellItem source = CreateShellItem(request.Source);
                        IShellItem folder = CreateShellItem(request.DestinationFolder);
                        if (source == null || folder == null)
                        {
                            if (source != null) Marshal.ReleaseComObject(source);
                            if (folder != null) Marshal.ReleaseComObject(folder);
                            result.Error = "无法创建 Shell 项";
                            continue;
                        }
                        liveItems.Add(source);
                        liveItems.Add(folder);
                        int queueResult = move
                            ? operation.MoveItem(source, folder, request.DestinationName, IntPtr.Zero)
                            : operation.CopyItem(source, folder, request.DestinationName, IntPtr.Zero);
                        if (queueResult < 0) result.Error = DescribeHResult(queueResult);
                    }
                    int performResult = operation.PerformOperations();
                    bool aborted;
                    operation.GetAnyOperationsAborted(out aborted);
                    Reconcile(results, requests, move, aborted, performResult);
                }
                finally
                {
                    foreach (IShellItem item in liveItems) { try { Marshal.ReleaseComObject(item); } catch { } }
                    try { Marshal.ReleaseComObject(operation); } catch { }
                }
            }
            finally
            {
                if (initialized) try { CoUninitialize(); } catch { }
            }
            return results;
        }

        // 对账（DeskBox ReconcileShellTransferResults 语义）：Shell 对部分项可能漏报，
        // 以文件系统事实为准；用户在进度窗取消（aborted）的剩余项标记 Aborted。
        private static void Reconcile(List<TransferResult> results, IList<TransferRequest> requests, bool move, bool aborted, int performResult)
        {
            for (int i = 0; i < results.Count; i++)
            {
                TransferResult result = results[i];
                if (result.Completed || result.Error != null) continue;
                bool sourceGone = !File.Exists(result.Source) && !Directory.Exists(result.Source);
                bool destinationExists = File.Exists(result.Destination) || Directory.Exists(result.Destination);
                if (move && sourceGone && destinationExists) { result.Completed = true; continue; }
                if (!move && destinationExists) { result.Completed = true; continue; }
                if (aborted || performResult == -2147024891 /* 0x800704C7 ERROR_CANCELLED */) result.Aborted = true;
                else if (performResult < 0) result.Error = DescribeHResult(performResult);
                else result.Error = "Shell 未报告该项结果";
            }
        }

        private static IShellItem CreateShellItem(string path)
        {
            Guid interfaceId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            IShellItem item;
            int result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out item);
            if (result < 0) return null;
            return item;
        }

        private static string DescribeHResult(int hresult)
        {
            Exception mapped = Marshal.GetExceptionForHR(hresult);
            return mapped != null ? mapped.Message : String.Format("0x{0:X8}", hresult);
        }

        // IFileOperation vtable 共 20 个方法，必须按接口声明顺序完整占位（槽位错位即崩）。
        [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
        private class FileOperationClass
        {
        }

        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            [PreserveSig] int Advise(IntPtr sink, out int cookie);
            [PreserveSig] int Unadvise(int cookie);
            [PreserveSig] int SetOperationFlags(uint operationFlags);
            [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            [PreserveSig] int SetProgressDialog(IntPtr dialog);                    // IOperationProgressDialog* 占位
            [PreserveSig] int SetProperties(IntPtr propertyArray);                 // IPropertyChangeArray* 占位
            [PreserveSig] int SetOwnerWindow(IntPtr ownerWindow);
            [PreserveSig] int ApplyPropertiesToItem(IntPtr shellItem);
            [PreserveSig] int ApplyPropertiesToItems(IntPtr shellItemArray);
            [PreserveSig] int RenameItem(IntPtr shellItem, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
            [PreserveSig] int RenameItems(IntPtr shellItemArray, [MarshalAs(UnmanagedType.LPWStr)] string newName);
            [PreserveSig] int MoveItem(IShellItem shellItem, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
            [PreserveSig] int MoveItems(IntPtr shellItemArray, IShellItem destinationFolder, IntPtr sink);
            [PreserveSig] int CopyItem(IShellItem shellItem, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
            [PreserveSig] int CopyItems(IntPtr shellItemArray, IShellItem destinationFolder, IntPtr sink);
            [PreserveSig] int DeleteItem(IntPtr shellItem, IntPtr sink);
            [PreserveSig] int DeleteItems(IntPtr shellItemArray, IntPtr sink);
            [PreserveSig] int NewItem(IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, IntPtr sink);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr handler);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint signature, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint apartmentType);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    }
}
