using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NetPeek.Core
{
    internal interface ITableSource { List<EndpointRecord> Read(bool tcp, bool ipv6); }
    internal interface IProcessSource
    {
        Dictionary<int, long> CaptureStartTimes();
        ProcessObservation Read(int pid);
    }
    internal sealed class ProcessObservation
    {
        internal long StartTimeUtcTicks;
        internal string Path;
        internal string Error;
    }

    public sealed class NetworkCollector : IDisposable
    {
        private readonly object gate = new object();
        private readonly ITableSource tables;
        private readonly IProcessSource processes;
        private long sequence;
        private bool disposed;
        public NetworkCollector() : this(new NativeTables(), new NativeProcesses()) { }
        internal NetworkCollector(ITableSource tables, IProcessSource processes) { this.tables = tables; this.processes = processes; }

        public NetworkSnapshot Capture()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("NetworkCollector");
                NetworkSnapshot snapshot = new NetworkSnapshot();
                string epoch = (++sequence).ToString(CultureInfo.InvariantCulture);
                Dictionary<int, long> before;
                try { before = processes.CaptureStartTimes(); }
                catch (Exception ex)
                {
                    if (IsFatal(ex)) throw;
                    before = new Dictionary<int, long>();
                    snapshot.Warnings.Add("无法校验采集前的进程身份：" + ex.Message);
                }
                int successes = 0;
                for (int i = 0; i < 4; i++)
                {
                    bool tcp = i < 2, ipv6 = (i % 2) == 1;
                    try { snapshot.Endpoints.AddRange(tables.Read(tcp, ipv6)); successes++; }
                    catch (Exception ex)
                    {
                        if (IsFatal(ex)) throw;
                        snapshot.Warnings.Add((tcp ? "TCP" : "UDP") + (ipv6 ? " IPv6" : " IPv4") + " 端点表采集失败：" + ex.Message);
                    }
                }
                snapshot.IsComplete = successes == 4;
                // Metadata is scoped to this capture, never cached by PID across captures.
                // Opening the process before and after table reads detects exit/PID reuse.
                Dictionary<int, ProcessIdentity> identities = new Dictionary<int, ProcessIdentity>();
                HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (EndpointRecord endpoint in snapshot.Endpoints)
                {
                    int pid = endpoint.Process.ProcessId;
                    ProcessIdentity identity;
                    if (!identities.TryGetValue(pid, out identity))
                    {
                        long start;
                        before.TryGetValue(pid, out start);
                        ProcessObservation after;
                        try { after = processes.Read(pid); }
                        catch (Exception ex) { if (IsFatal(ex)) throw; after = new ProcessObservation { Error = ex.Message }; }
                        identity = BuildIdentity(pid, start, after, epoch);
                        identities.Add(pid, identity);
                    }
                    endpoint.Process = identity;
                    string baseKey = identity.InstanceId + "|" + endpoint.Protocol + "|" + endpoint.AddressFamily + "|" + endpoint.LocalAddress + ":" + endpoint.LocalPort + "|" + endpoint.RemoteAddress + ":" + endpoint.RemotePort;
                    string key = baseKey;
                    int duplicate = 1;
                    while (!keys.Add(key)) key = baseKey + "|" + (++duplicate).ToString(CultureInfo.InvariantCulture);
                    endpoint.Key = key;
                }
                snapshot.CapturedAtUtc = DateTime.UtcNow;
                return snapshot;
            }
        }

        internal static ProcessIdentity BuildIdentity(int pid, long before, ProcessObservation after, string epoch)
        {
            ProcessIdentity identity = new ProcessIdentity { ProcessId = pid };
            bool verified = before > 0 && after != null && before == after.StartTimeUtcTicks;
            identity.StartTimeUtcTicks = verified ? before : 0;
            identity.InstanceId = verified ? pid + ":" + before : "unknown:" + pid + ":" + epoch;
            identity.AppKey = "instance:" + identity.InstanceId;
            identity.Name = "未知进程 (PID " + pid + ")";
            if (!verified)
            {
                identity.AccessNote = after != null && !String.IsNullOrEmpty(after.Error) ? after.Error : "进程已退出、刚启动或身份发生变化，无法可靠归属";
                return identity;
            }
            if (String.IsNullOrWhiteSpace(after.Path))
            {
                identity.AccessNote = String.IsNullOrEmpty(after.Error) ? "无法读取进程路径" : after.Error;
                return identity;
            }
            try
            {
                string path = after.Path;
                if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
                // QueryFullProcessImageName returns an absolute Win32 path. Do not resolve
                // untrusted relative paths into the application's working directory.
                if (!System.IO.Path.IsPathRooted(path)) throw new ArgumentException("进程路径不是绝对路径");
                identity.Path = System.IO.Path.GetFullPath(path);
                identity.AppKey = "path:" + identity.Path.ToUpperInvariant();
                identity.Name = System.IO.Path.GetFileNameWithoutExtension(identity.Path);
            }
            catch (Exception ex)
            {
                if (IsFatal(ex)) throw;
                identity.Path = null;
                identity.AccessNote = "无法规范化进程路径：" + ex.Message;
            }
            return identity;
        }

        private static bool IsFatal(Exception ex) { return ex is OutOfMemoryException || ex is StackOverflowException || ex is System.Threading.ThreadAbortException; }
        public void Dispose() { lock (gate) { disposed = true; } }
    }
}
