using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NetPeek.Core
{
    internal sealed class NativeProcesses : IProcessSource
    {
        [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumProcesses([Out] int[] ids, int size, out int needed);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        public Dictionary<int, long> CaptureStartTimes()
        {
            int[] ids = null;
            int needed = 0;
            for (int size = 1024; size <= 131072; size *= 2)
            {
                ids = new int[size];
                if (!EnumProcesses(ids, size * 4, out needed)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (needed < size * 4) break;
                if (size == 131072) throw new InvalidOperationException("进程数量超出安全范围");
            }
            Dictionary<int, long> result = new Dictionary<int, long>();
            for (int i = 0; i < needed / 4; i++)
            {
                ProcessObservation info = ReadCore(ids[i], false);
                if (info.StartTimeUtcTicks > 0) result[ids[i]] = info.StartTimeUtcTicks;
            }
            return result;
        }
        public ProcessObservation Read(int pid) { return ReadCore(pid, true); }
        internal static string NormalizeImagePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("进程路径为空");
            // Normalize extended UNC before removing the generic extended-path prefix.
            // This is lexical only: never open the file or contact its server/share.
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            bool drive = path.Length >= 3 && (path[0] >= 'A' && path[0] <= 'Z' || path[0] >= 'a' && path[0] <= 'z') && path[1] == ':' && path[2] == '\\';
            bool unc = path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);
            if (!drive && !unc) throw new ArgumentException("进程路径不是完整绝对路径");
            // Avoid GetFullPath's legacy short-name expansion on network paths.
            // Normalize only path syntax, never resolve files, links or 8.3 aliases.
            string[] parts = path.Substring(drive ? 3 : 2).Split('\\');
            List<string> normalized = new List<string>();
            int rootParts = drive ? 0 : 2;
            foreach (string part in parts) {
                if (part.Length == 0 || part == ".") {
                    if (normalized.Count < rootParts) throw new ArgumentException("UNC 服务器或共享名无效");
                    continue;
                }
                if (part == "..") {
                    if (normalized.Count < rootParts) throw new ArgumentException("UNC 服务器或共享名无效");
                    if (normalized.Count > rootParts) normalized.RemoveAt(normalized.Count - 1);
                    continue;
                }
                if (part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("进程路径包含无效字符");
                normalized.Add(part);
            }
            if (normalized.Count <= rootParts) throw new ArgumentException("进程路径缺少可执行文件名");
            return (drive ? path.Substring(0, 3) : @"\\") + String.Join(@"\", normalized);
        }
        private static ProcessObservation ReadCore(int pid, bool includePath)
        {
            ProcessObservation result = new ProcessObservation();
            if (pid <= 0) { result.Error = "系统未提供可验证的进程归属"; return result; }
            IntPtr handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION; never request elevation.
            if (handle == IntPtr.Zero) { result.Error = "进程已退出或访问受限 (Win32 " + Marshal.GetLastWin32Error() + ")"; return result; }
            try
            {
                long creation, exit, kernel, user;
                if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user)) { result.Error = "无法校验进程启动时间 (Win32 " + Marshal.GetLastWin32Error() + ")"; return result; }
                if (exit != 0) { result.Error = "进程已退出"; return result; }
                result.StartTimeUtcTicks = DateTime.FromFileTimeUtc(creation).Ticks;
                if (includePath)
                {
                    int length = 32768;
                    StringBuilder path = new StringBuilder(length);
                    if (QueryFullProcessImageName(handle, 0, path, ref length)) result.Path = path.ToString();
                    else result.Error = "进程路径访问受限 (Win32 " + Marshal.GetLastWin32Error() + ")";
                    // A process may exit while its path is read. Keep identity only if still alive.
                    if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user) || exit != 0) { result.StartTimeUtcTicks = 0; result.Path = null; result.Error = "采集过程中进程已退出"; }
                }
                return result;
            }
            finally { CloseHandle(handle); }
        }
    }
}
