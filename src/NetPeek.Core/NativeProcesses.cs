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
