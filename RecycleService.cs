using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace BaobaoDesktop
{
    public sealed class RecycleItemResult
    {
        public string Path;
        public bool Success;
        public string Message;
    }

    public sealed class RecycleBatchResult
    {
        public List<RecycleItemResult> Items = new List<RecycleItemResult>();
        public int SuccessCount;
        public int FailureCount;
    }

    /// <summary>
    /// Conservative, file-only Windows recycle service. Never calls a permanent
    /// delete API. The caller must run Recycle on its own STA thread.
    /// </summary>
    public static class RecycleService
    {
        private const uint INVALID_FILE_ATTRIBUTES = 0xffffffff;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        private const uint FILE_ATTRIBUTE_SYSTEM = 0x4;
        private const uint FILE_ATTRIBUTE_READONLY = 0x1;
        private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
        private const int E_ABORT = unchecked((int)0x80004004);
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        // FOFX_RECYCLEONDELETE is Windows 8+ and asks for recycling rather than
        // permanent deletion. WANTNUKEWARNING overrides automatic confirmation
        // for a destruction warning. The sink also rejects non-recycle deletes.
        private const uint OPERATION_FLAGS =
            0x00080000 | // FOFX_RECYCLEONDELETE
            0x20000000 | // FOFX_ADDUNDORECORD
            0x00100000 | // FOFX_EARLYFAILURE
            0x00000004 | // FOF_SILENT
            0x00000010 | // FOF_NOCONFIRMATION (not destruction warnings)
            0x00000400 | // FOF_NOERRORUI
            0x00002000 | // FOF_NO_CONNECTED_ELEMENTS: no related HTML folders
            0x00004000;  // FOF_WANTNUKEWARNING

        private static readonly string OwnExecutable = SafeOwnExecutable();
        private static readonly string OwnDirectory = String.IsNullOrEmpty(OwnExecutable) ? null : System.IO.Path.GetDirectoryName(OwnExecutable);

        public static string ValidatePath(string path)
        {
            string full;
            string error = NormalizePath(path, out full);
            if (error != null) return error;
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT || Environment.OSVersion.Version < new Version(6, 2))
                    return "当前系统不支持本程序要求的安全回收接口。";

                string root = System.IO.Path.GetPathRoot(full);
                if (String.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return "不能回收磁盘根目录。";

                if ((!String.IsNullOrEmpty(OwnExecutable) && SamePath(full, OwnExecutable)) ||
                    (!String.IsNullOrEmpty(OwnDirectory) && IsWithin(full, OwnDirectory)))
                    return "不能回收包包程序及其所在文件夹中的文件。";

                foreach (string protectedPath in ProtectedDirectories(root))
                    if (!String.IsNullOrEmpty(protectedPath) && IsWithin(full, protectedPath))
                        return "不能回收系统或程序目录中的文件。";

                DriveInfo drive = new DriveInfo(root);
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    return "只接收本地固定磁盘上的普通文件；网络盘和可移动磁盘暂不支持。";

                StringBuilder device = new StringBuilder(1024);
                if (QueryDosDevice(root.Substring(0, 2), device, device.Capacity) == 0)
                    return "无法核实磁盘类型，已停止回收。";
                if (device.ToString().StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase) ||
                    device.ToString().IndexOf("Mup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    device.ToString().IndexOf("LanmanRedirector", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "不接收映射盘、替代盘或网络重定向路径。";

                // Inspect every ancestor, not only the final file. Do not
                // traverse junctions, symbolic links or cloud reparse points.
                string current = root;
                string[] components = full.Substring(root.Length).Split('\\');
                for (int i = 0; i < components.Length; i++)
                {
                    current = System.IO.Path.Combine(current, components[i]);
                    uint attributes = GetFileAttributes(current);
                    if (attributes == INVALID_FILE_ATTRIBUTES)
                    {
                        int nativeError = Marshal.GetLastWin32Error();
                        if (nativeError == 2 || nativeError == 3) return "文件不存在或已经被移动。";
                        return "无法读取文件或上级目录，已停止回收。";
                    }
                    if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                        return "不接收符号链接、目录联接或其他重解析路径。";
                    if (i == components.Length - 1)
                    {
                        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                            return "当前版本仅接收普通文件，文件夹暂不支持。";
                        if ((attributes & FILE_ATTRIBUTE_SYSTEM) != 0)
                            return "不能回收带系统属性的文件。";
                        if ((attributes & FILE_ATTRIBUTE_READONLY) != 0)
                            return "文件为只读状态，请先在资源管理器中处理。";
                    }
                    else if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
                        return "上级路径不是文件夹。";
                }

                long length = new FileInfo(full).Length;
                error = CheckRecycleAvailability(root, length);
                return error;
            }
            catch (UnauthorizedAccessException) { return "没有权限读取这个文件或回收站设置。"; }
            catch (Exception) { return "无法确认此文件可安全移入回收站，已停止操作。"; }
        }

        public static RecycleBatchResult Recycle(string[] paths, IntPtr owner)
        {
            RecycleBatchResult batch = new RecycleBatchResult();
            if (paths == null || paths.Length == 0) return batch;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string input in paths)
            {
                string normalized;
                string normalizationError = NormalizePath(input, out normalized);
                string key = normalized ?? (input ?? "<null>");
                if (!seen.Add(key)) continue;
                RecycleItemResult result = new RecycleItemResult();
                result.Path = normalized ?? input ?? "";
                result.Success = false;
                result.Message = normalizationError;
                batch.Items.Add(result);
            }
            // Canonicalize/deduplicate the whole batch before the first file is
            // moved. Otherwise a later 8.3 alias could no longer be expanded
            // after its long-name counterpart had already been recycled.
            foreach (RecycleItemResult result in batch.Items)
            {
                string error = result.Message ?? ValidatePath(result.Path);
                if (error != null) result.Message = error;
                else if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                    result.Message = "回收服务需要在独立的 STA 线程中运行。";
                else
                    RecycleOne(result.Path, owner, result);
                if (result.Success) batch.SuccessCount++; else batch.FailureCount++;
            }
            return batch;
        }

        private static void RecycleOne(string full, IntPtr owner, RecycleItemResult result)
        {
            IFileOperation operation = null;
            IShellItem item = null;
            RecycleProgressSink sink = new RecycleProgressSink(full);
            try
            {
                int hr;
                Guid iid = typeof(IShellItem).GUID;
                hr = SHCreateItemFromParsingName(full, IntPtr.Zero, ref iid, out item);
                if (hr < 0 || item == null) { result.Message = FailureMessage(hr); return; }
                string shellPath = GetShellPath(item);
                if (!SamePath(full, shellPath))
                {
                    result.Message = "系统解析的文件路径不一致，已停止回收。";
                    return;
                }
                operation = (IFileOperation)new FileOperationCom();
                hr = operation.SetOperationFlags(OPERATION_FLAGS);
                if (hr < 0) { result.Message = FailureMessage(hr); return; }
                if (owner != IntPtr.Zero)
                {
                    hr = operation.SetOwnerWindow(owner);
                    if (hr < 0) { result.Message = FailureMessage(hr); return; }
                }
                hr = operation.DeleteItem(item, sink);
                if (hr < 0) { result.Message = FailureMessage(hr); return; }
                int performHr = operation.PerformOperations();
                bool aborted;
                int abortedHr = operation.GetAnyOperationsAborted(out aborted);
                bool gone = IsDefinitelyMissing(full);
                // PostDeleteItem supplies the actual operation result; a queued
                // DeleteItem S_OK, PerformOperations S_OK, or missing path alone
                // is not sufficient. A non-null newly-created item is documented
                // to identify the recycled item, not a permanently deleted file.
                result.Success = performHr >= 0 && abortedHr >= 0 && !aborted &&
                    sink.PreDeleteAllowed && sink.PostDeleteSeen && sink.DeleteHr >= 0 &&
                    sink.RecycledItemReturned && gone;
                if (result.Success) result.Message = "已移入系统回收站。";
                else if (!String.IsNullOrEmpty(sink.Error)) result.Message = sink.Error;
                else if (sink.PostDeleteSeen && sink.DeleteHr < 0) result.Message = FailureMessage(sink.DeleteHr);
                else if (performHr < 0) result.Message = FailureMessage(performHr);
                else if (abortedHr < 0 || aborted) result.Message = "回收操作已取消或未完成。";
                else if (!gone) result.Message = "文件仍在原位置，未完成回收。";
                else result.Message = "系统未返回可验证的回收站结果，请检查系统回收站。";
            }
            catch (COMException e) { result.Message = FailureMessage(e.ErrorCode); }
            catch (Exception) { result.Message = "系统未能完成安全回收；请在资源管理器中检查该文件。"; }
            finally
            {
                GC.KeepAlive(sink);
                if (operation != null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
                if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
            }
        }

        private static string NormalizePath(string path, out string full)
        {
            full = null;
            if (String.IsNullOrWhiteSpace(path)) return "没有收到有效的文件路径。";
            if (path.Length < 3 || !Char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
                return "只接收带盘符的本地绝对文件路径。";
            if (path.IndexOf(':', 2) >= 0 || path.IndexOf('*') >= 0 || path.IndexOf('?') >= 0 || path.IndexOf('\0') >= 0)
                return "不接收设备路径、通配符或备用数据流。";
            string[] pieces = path.Replace('/', '\\').Substring(3).Split('\\');
            foreach (string piece in pieces)
                if (piece.Length > 0 && piece != "." && piece != ".." && (piece.EndsWith(" ", StringComparison.Ordinal) || piece.EndsWith(".", StringComparison.Ordinal)))
                    return "路径包含不明确的末尾空格或句点，已停止操作。";
            try
            {
                full = System.IO.Path.GetFullPath(path).Replace('/', '\\').TrimEnd('\\');
                if (full.Length == 2) full += "\\";
                // Expand existing 8.3 aliases before applying protected-directory
                // checks and batch deduplication. A lexical GetFullPath alone
                // would leave aliases such as PROGRA~1 unprotected.
                StringBuilder longName = new StringBuilder(32768);
                uint longLength = GetLongPathName(full, longName, longName.Capacity);
                if (longLength > 0 && longLength < longName.Capacity)
                    full = longName.ToString().TrimEnd('\\');
                else if (GetFileAttributes(full) != INVALID_FILE_ATTRIBUTES)
                { full = null; return "无法标准化文件路径，已停止操作。"; }
                if (full != null && full.Length == 2) full += "\\";
                if (full.Length >= 260) { full = null; return "当前版本暂不接收超过系统常规长度限制的路径。"; }
                return null;
            }
            catch (Exception) { return "文件路径格式无效。"; }
        }

        private static IEnumerable<string> ProtectedDirectories(string root)
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            yield return System.IO.Path.Combine(root, "$Recycle.Bin");
            yield return System.IO.Path.Combine(root, "System Volume Information");
            yield return System.IO.Path.Combine(root, "Recovery");
        }

        private static string SafeOwnExecutable()
        {
            try { return System.IO.Path.GetFullPath(Assembly.GetExecutingAssembly().Location); }
            catch (Exception) { return null; }
        }

        private static bool SamePath(string left, string right)
        {
            if (String.IsNullOrEmpty(left) || String.IsNullOrEmpty(right)) return false;
            try { return String.Equals(System.IO.Path.GetFullPath(left).TrimEnd('\\'), System.IO.Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        private static bool IsWithin(string path, string directory)
        {
            string normalized = System.IO.Path.GetFullPath(directory).TrimEnd('\\');
            return String.Equals(path, normalized, StringComparison.OrdinalIgnoreCase) || path.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string CheckRecycleAvailability(string root, long fileLength)
        {
            const string policyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer";
            if (ReadNonzero(Registry.CurrentUser, policyPath, "NoRecycleFiles") || ReadNonzero(Registry.LocalMachine, policyPath, "NoRecycleFiles"))
                return "系统策略已禁用回收站，文件保持原位。";
            const string bucketPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\BitBucket";
            if (ReadNonzero(Registry.CurrentUser, bucketPath, "NukeOnDelete"))
                return "回收站设置为直接删除，已停止操作。";
            StringBuilder volume = new StringBuilder(128);
            if (!GetVolumeNameForVolumeMountPoint(root, volume, volume.Capacity))
                return "无法核实此磁盘的回收站设置，已停止操作。";
            string volumeName = volume.ToString();
            int start = volumeName.IndexOf('{'), end = volumeName.IndexOf('}');
            if (start < 0 || end <= start) return "无法核实磁盘标识，已停止操作。";
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(bucketPath + "\\Volume\\" + volumeName.Substring(start, end - start + 1)))
            {
                if (key != null)
                {
                    object nuke = key.GetValue("NukeOnDelete");
                    if (nuke != null && Convert.ToInt64(nuke) != 0)
                        return "这个磁盘的回收站设置为直接删除，已停止操作。";
                    object capacity = key.GetValue("MaxCapacity");
                    if (capacity != null)
                    {
                        long capacityMb = Convert.ToInt64(capacity);
                        if (capacityMb <= 0 || (capacityMb < Int64.MaxValue / (1024 * 1024) && fileLength > capacityMb * 1024 * 1024))
                            return "文件超过这个磁盘的回收站容量限制，已停止操作。";
                    }
                }
            }
            long count;
            int hr = QueryRecycleCount(root, out count);
            return hr < 0 ? "无法访问这个磁盘的系统回收站，文件保持原位。" : null;
        }

        private static bool ReadNonzero(RegistryKey hive, string subkey, string name)
        {
            using (RegistryKey key = hive.OpenSubKey(subkey))
            {
                object value = key == null ? null : key.GetValue(name);
                return value != null && Convert.ToInt64(value) != 0;
            }
        }

        internal static int QueryRecycleCount(string root, out long count)
        {
            // shellapi.h uses packing 1 for x86, default packing for x64.
            int offset = IntPtr.Size == 8 ? 8 : 4;
            int size = IntPtr.Size == 8 ? 24 : 20;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, size);
                int hr = SHQueryRecycleBin(root, buffer);
                count = hr >= 0 ? Marshal.ReadInt64(buffer, offset + 8) : -1;
                return hr;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static bool IsDefinitelyMissing(string path)
        {
            uint attributes = GetFileAttributes(path);
            if (attributes != INVALID_FILE_ATTRIBUTES) return false;
            int error = Marshal.GetLastWin32Error();
            return error == 2 || error == 3;
        }

        private static string GetShellPath(IShellItem item)
        {
            IntPtr name;
            int hr = item.GetDisplayName(SIGDN_FILESYSPATH, out name);
            if (hr < 0 || name == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(name); }
            finally { Marshal.FreeCoTaskMem(name); }
        }

        private static string FailureMessage(int hr)
        {
            if (hr == unchecked((int)0x80070020) || hr == unchecked((int)0x80070021) || hr == unchecked((int)0x80270027)) return "文件正在被占用，未完成回收。";
            if (hr == unchecked((int)0x80070005)) return "没有权限回收这个文件。";
            if (hr == E_ABORT || hr == unchecked((int)0x800704c7)) return "回收操作已取消。";
            return "系统未能安全回收此文件（0x" + unchecked((uint)hr).ToString("X8") + "）。";
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class RecycleProgressSink : IFileOperationProgressSink
        {
            private readonly string expectedPath;
            internal bool PreDeleteAllowed;
            internal bool PostDeleteSeen;
            internal bool RecycledItemReturned;
            internal int DeleteHr = E_ABORT;
            internal string Error;
            internal RecycleProgressSink(string path) { expectedPath = path; }
            public int PreDeleteItem(uint flags, IShellItem item)
            {
                try
                {
                    if ((flags & TSF_DELETE_RECYCLE_IF_POSSIBLE) == 0)
                    { Error = "系统没有确认回收方式，已取消删除。"; return E_ABORT; }
                    if (!SamePath(expectedPath, GetShellPath(item)))
                    { Error = "文件路径发生变化，已取消回收。"; return E_ABORT; }
                    string validation = ValidatePath(expectedPath);
                    if (validation != null) { Error = validation; return E_ABORT; }
                    PreDeleteAllowed = true;
                    return 0;
                }
                catch (Exception) { Error = "无法复核文件状态，已取消回收。"; return E_ABORT; }
            }
            public int PostDeleteItem(uint flags, IShellItem item, int hrDelete, IShellItem newlyCreated)
            {
                PostDeleteSeen = true; DeleteHr = hrDelete;
                RecycledItemReturned = newlyCreated != null;
                if (hrDelete >= 0 && newlyCreated == null)
                    Error = "系统未返回回收站中的文件，不能确认安全回收。";
                return 0;
            }
            public int StartOperations() { return 0; }
            public int FinishOperations(int hr) { return 0; }
            public int PreRenameItem(uint flags, IShellItem item, string name) { return E_ABORT; }
            public int PostRenameItem(uint flags, IShellItem item, string name, int hr, IShellItem created) { return 0; }
            public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name) { return E_ABORT; }
            public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int hr, IShellItem created) { return 0; }
            public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) { return E_ABORT; }
            public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int hr, IShellItem created) { return 0; }
            public int PreNewItem(uint flags, IShellItem destination, string name) { return E_ABORT; }
            public int PostNewItem(uint flags, IShellItem destination, string name, string templateName, uint attributes, int hr, IShellItem created) { return 0; }
            public int UpdateProgress(uint total, uint completed) { return 0; }
            public int ResetTimer() { return 0; }
            public int PauseTimer() { return 0; }
            public int ResumeTimer() { return 0; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int maximumLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, int bufferLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int bufferLength);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW", ExactSpelling = true)]
        private static extern int SHQueryRecycleBin(string rootPath, IntPtr info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindingContext, ref Guid iid, out IShellItem item);

        [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), ClassInterface(ClassInterfaceType.None)]
        private class FileOperationCom { }

        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetOperationFlags(uint flags);
            [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            [PreserveSig] int SetProgressDialog(IntPtr dialog);
            [PreserveSig] int SetProperties(IntPtr properties);
            [PreserveSig] int SetOwnerWindow(IntPtr owner);
            [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
            [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
            [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
            [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
            [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
            [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
            [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
            [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
            [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
            [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, IFileOperationProgressSink sink);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }

        [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperationProgressSink
        {
            [PreserveSig] int StartOperations();
            [PreserveSig] int FinishOperations(int result);
            [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
            [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
            [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
            [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
            [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created);
            [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, uint attributes, int result, IShellItem created);
            [PreserveSig] int UpdateProgress(uint total, uint completed);
            [PreserveSig] int ResetTimer();
            [PreserveSig] int PauseTimer();
            [PreserveSig] int ResumeTimer();
        }
    }
}
