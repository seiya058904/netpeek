using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetPeek.Core;

namespace NetPeek
{
    public abstract class Bindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed(string name) { var handler = PropertyChanged; if (handler != null) handler(this, new PropertyChangedEventArgs(name)); }
        protected bool Set<T>(ref T field, T value, string name) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    }

    public sealed class EndpointView : Bindable
    {
        public EndpointRecord Record { get; private set; }
        public string Key { get; private set; }
        public DateTime LastSeenUtc { get; private set; }
        private bool current;
        public bool IsCurrent { get { return current; } set { if (Set(ref current, value, "IsCurrent")) { Changed("Status"); Changed("TimeText"); Changed("Opacity"); } } }
        public string Transport { get { return Record.Protocol + " / " + Record.AddressFamily; } }
        public string Local { get { return Format(Record.LocalAddress, Record.LocalPort); } }
        public string Remote { get { return Record.Protocol == "UDP" ? "未知 · 系统表不提供" : Record.State == "LISTEN" ? "—" : Format(Record.RemoteAddress, Record.RemotePort); } }
        public string Scope { get { return Record.Protocol == "UDP" || Record.State == "LISTEN" ? "本地端点" : AddressScope(Record.RemoteAddress); } }
        public string Status { get { return !IsCurrent ? "历史出现" : StateLabel(Record.State); } }
        public string TimeText { get { return IsCurrent ? "本次快照" : LastSeenUtc.ToLocalTime().ToString("HH:mm:ss"); } }
        public double Opacity { get { return IsCurrent ? 1 : 0.62; } }
        public string CopyText { get { return Record.Protocol == "UDP" || Record.State == "LISTEN" ? Local : Remote; } }
        public EndpointView(EndpointRecord record, DateTime time) : this(record, time, record.Key) { }
        internal EndpointView(EndpointRecord record, DateTime time, string displayKey) { Key = displayKey; Record = record; LastSeenUtc = time; current = true; }
        public void Update(EndpointRecord record, DateTime time) { Update(record, time, true); }
        internal bool Update(EndpointRecord record, DateTime time, bool isCurrent)
        {
            bool state = Record.State != record.State;
            bool transport = Record.Protocol != record.Protocol || Record.AddressFamily != record.AddressFamily;
            bool local = Record.LocalAddress != record.LocalAddress || Record.LocalPort != record.LocalPort;
            bool remote = Record.RemoteAddress != record.RemoteAddress || Record.RemotePort != record.RemotePort;
            bool currentChanged = current != isCurrent;
            string previousTime = TimeText;
            string previousRemote = remote || transport || state ? Remote : null;
            string previousScope = remote || transport || state ? Scope : null;
            string previousCopy = local || remote || transport || state ? CopyText : null;
            string previousStatus = state || currentChanged ? Status : null;
            Record = record; LastSeenUtc = time; current = isCurrent;
            if (transport) Changed("Transport");
            if (local) Changed("Local");
            if ((remote || transport || state) && previousRemote != Remote) Changed("Remote");
            if ((remote || transport || state) && previousScope != Scope) Changed("Scope");
            if ((local || remote || transport || state) && previousCopy != CopyText) Changed("CopyText");
            if ((state || currentChanged) && previousStatus != Status) Changed("Status");
            if (currentChanged) { Changed("IsCurrent"); Changed("Opacity"); }
            if (previousTime != TimeText) Changed("TimeText");
            return state || transport || local || remote || currentChanged;
        }
        public static string Format(string address, int port) { if (String.IsNullOrEmpty(address)) return "未知"; return (address.Contains(":") ? "[" + address + "]" : address) + ":" + port; }
        public static string StateLabel(string state)
        {
            switch (state) {
                case "ESTABLISHED": return "已建立"; case "LISTEN": return "监听"; case "BOUND": return "UDP 本地绑定";
                case "SYN_SENT": return "正在连接"; case "SYN_RECEIVED": case "SYN_RCVD": return "正在握手";
                case "TIME_WAIT": return "等待回收"; case "CLOSE_WAIT": return "等待关闭"; case "CLOSED": return "已关闭";
                case "FIN_WAIT1": case "FIN_WAIT_1": case "FIN_WAIT2": case "FIN_WAIT_2": case "CLOSING": case "LAST_ACK": return "正在关闭";
                case "DELETE_TCB": return "待删除"; default: return state ?? "未知";
            }
        }
        public static string AddressScope(string text)
        {
            IPAddress address;
            if (!IPAddress.TryParse(text, out address)) return "未知范围";
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return "本机回环";
            byte[] b = address.GetAddressBytes();
            if (b.Length == 4) {
                if (b[0] == 0) return "未指定";
                if (b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168) return "私有地址";
                if (b[0] == 169 && b[1] == 254) return "链路本地";
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "共享地址";
                if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return "基准测试地址";
                if (b[0] == 192 && b[1] == 0 && b[2] == 2 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113) return "文档示例地址";
                if (b[0] >= 224) return "组播 / 保留";
                return "外部地址";
            }
            if (address.Equals(IPAddress.IPv6Any)) return "未指定";
            if (address.IsIPv6LinkLocal) return "链路本地";
            if ((b[0] & 0xfe) == 0xfc) return "私有地址";
            if (address.IsIPv6Multicast) return "组播";
            return "外部地址";
        }
    }

    public sealed class ProcessView : Bindable
    {
        public ProcessIdentity Identity { get; private set; }
        public string Key { get; private set; }
        public string Title { get { return "PID " + Identity.ProcessId + "  ·  " + Identity.Name; } }
        public string Note { get { return Identity.StartTimeUtcTicks <= 0 ? (Identity.AccessNote != null && Identity.AccessNote.Contains("身份不可核验") ? Identity.AccessNote : "身份不可核验 · " + (Identity.AccessNote ?? "仅代表本次快照，不确定归属")) : String.IsNullOrEmpty(Identity.AccessNote) ? "进程创建于 " + new DateTime(Identity.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss") : Identity.AccessNote; } }
        private bool expanded;
        public bool IsExpanded { get { return expanded; } set { Set(ref expanded, value, "IsExpanded"); } }
        public ObservableCollection<EndpointView> Endpoints { get; private set; }
        private int currentCount;
        public string CountText { get { return currentCount + " 个当前端点"; } }
        public ProcessView(ProcessIdentity identity) : this(identity, identity.InstanceId) { }
        internal ProcessView(ProcessIdentity identity, string displayKey) { Key = displayKey; Identity = identity; Endpoints = new ObservableCollection<EndpointView>(); }
        public bool UpdateIdentity(ProcessIdentity identity)
        {
            bool titleChanged = Identity.ProcessId != identity.ProcessId || Identity.Name != identity.Name;
            bool noteChanged = Identity.StartTimeUtcTicks != identity.StartTimeUtcTicks || Identity.AccessNote != identity.AccessNote;
            string oldNote = noteChanged ? Note : null;
            bool searchChanged = titleChanged || Identity.Path != identity.Path;
            Identity = identity;
            if (titleChanged) Changed("Title");
            if (noteChanged && oldNote != Note) Changed("Note");
            return searchChanged;
        }
        internal void SetCurrentCount(int count) { if (currentCount != count) { currentCount = count; Changed("CountText"); } }
        public void NotifyCounts() { SetCurrentCount(Endpoints.Count(x => x.IsCurrent)); }
    }

    public sealed class ApplicationView : Bindable
    {
        public string Key { get; private set; }
        public string Name { get; private set; }
        public string Path { get; private set; }
        public string PathText { get { return String.IsNullOrEmpty(Path) ? "路径未知 · 进程已退出或访问受限" : Path; } }
        public string Initial { get { return String.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant(); } }
        private ImageSource icon;
        public ImageSource Icon { get { return icon; } set { Set(ref icon, value, "Icon"); } }
        private bool expanded;
        public bool IsExpanded { get { return expanded; } set { if (Set(ref expanded, value, "IsExpanded") && value && Processes.Count == 1) Processes[0].IsExpanded = true; } }
        private int established, listening, udp, currentCount, processCount;
        private static readonly Brush ConnectedBrush = FrozenBrush(0xE1, 0xF1, 0xE7);
        private static readonly Brush HistoryBrush = FrozenBrush(0xEF, 0xF0, 0xED);
        private static readonly Brush OtherBrush = FrozenBrush(0xED, 0xF1, 0xF7);
        private static Brush FrozenBrush(byte r, byte g, byte b) { SolidColorBrush result = new SolidColorBrush(Color.FromRgb(r, g, b)); result.Freeze(); return result; }
        public int Established { get { return established; } }
        public int CurrentCount { get { return currentCount; } }
        public string CountText { get { return currentCount.ToString(); } }
        public string CountLabel { get { return currentCount > 0 ? "当前端点" : "仅近期记录"; } }
        public string Subtitle { get { return Processes.Count + " 个进程" + (String.IsNullOrEmpty(Path) ? "  ·  归属信息受限" : "  ·  " + System.IO.Path.GetFileName(Path)); } }
        public string Status { get { return established > 0 ? "TCP 已建立" : currentCount == 0 ? "历史出现" : listening > 0 && udp == 0 && currentCount == listening ? "仅监听" : udp > 0 && currentCount == udp ? "UDP 本地端点" : "本地 / 其他状态"; } }
        public Brush StatusBrush { get { return established > 0 ? ConnectedBrush : currentCount == 0 ? HistoryBrush : OtherBrush; } }
        public ObservableCollection<ProcessView> Processes { get; private set; }
        public ApplicationView(ProcessIdentity identity) : this(identity, identity.AppKey) { }
        internal ApplicationView(ProcessIdentity identity, string displayKey) { Key = displayKey; Name = identity.Name; Path = identity.Path; Processes = new ObservableCollection<ProcessView>(); }
        internal bool UpdateMetadata(ProcessIdentity identity)
        {
            bool changed = false;
            if (!String.Equals(Name, identity.Name, StringComparison.OrdinalIgnoreCase)) { string oldInitial = Initial; Name = identity.Name; Changed("Name"); if (oldInitial != Initial) Changed("Initial"); changed = true; }
            if (!String.Equals(Path, identity.Path, StringComparison.OrdinalIgnoreCase)) { Path = identity.Path; Changed("Path"); Changed("PathText"); Changed("Subtitle"); changed = true; }
            return changed;
        }
        public bool Recount()
        {
            int oldCurrent = currentCount, oldEstablished = established, oldProcesses = processCount;
            string oldStatus = Status; Brush oldBrush = StatusBrush;
            currentCount = established = listening = udp = 0;
            foreach (var process in Processes) {
                int count = 0;
                foreach (var endpoint in process.Endpoints) {
                    if (!endpoint.IsCurrent) continue;
                    count++; currentCount++;
                    if (endpoint.Record.Protocol == "TCP" && endpoint.Record.State == "ESTABLISHED") established++;
                    if (endpoint.Record.State == "LISTEN") listening++;
                    if (endpoint.Record.Protocol == "UDP") udp++;
                }
                process.SetCurrentCount(count);
            }
            processCount = Processes.Count;
            if (oldCurrent != currentCount) { Changed("CurrentCount"); Changed("CountText"); if ((oldCurrent == 0) != (currentCount == 0)) Changed("CountLabel"); }
            if (oldEstablished != established) Changed("Established");
            if (oldProcesses != processCount) Changed("Subtitle");
            if (oldStatus != Status) Changed("Status");
            if (!Object.ReferenceEquals(oldBrush, StatusBrush)) Changed("StatusBrush");
            return oldCurrent != currentCount || oldEstablished != established || oldProcesses != processCount || oldStatus != Status;
        }
        public bool Matches(string search)
        {
            if (String.IsNullOrEmpty(search)) return true;
            if ((Name + " " + Path).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return Processes.Any(p => p.Identity.ProcessId.ToString().Contains(search) || p.Endpoints.Any(e => (e.Local + " " + e.Remote + " " + e.Transport).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
        }
    }

    // Stable view objects retain expansion, selection, focus and scroll across snapshots.
    public sealed class ConnectionStore
    {
        public const int HistoryLimit = CaptureHistory.HistoryLimit;
        public const int HistorySeconds = CaptureHistory.HistorySeconds;
        public ObservableCollection<ApplicationView> Applications { get; private set; }
        private readonly Dictionary<string, ApplicationView> applications = new Dictionary<string, ApplicationView>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProcessView> processes = new Dictionary<string, ProcessView>();
        private readonly Dictionary<string, EndpointView> endpoints = new Dictionary<string, EndpointView>();
        private readonly Dictionary<string, DateTime> identityTimes = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, ApplicationView> processOwners = new Dictionary<string, ApplicationView>();
        private DateTime truncatedUntilUtc = DateTime.MinValue;
        private sealed class Pending { internal EndpointRecord Record; internal DateTime Seen; internal bool Current; }
        public bool HistoryTruncated { get; private set; }
        public ConnectionStore() { Applications = new ObservableCollection<ApplicationView>(); }
        public bool Apply(NetworkSnapshot snapshot)
        {
            return Apply(snapshot, null);
        }
        // True means search/filter membership or visible summaries may have changed.
        // Timestamp-only sampling does not require rebuilding the filtered collection.
        public bool Apply(NetworkSnapshot snapshot, IList<Observation> observed)
        {
            if (!snapshot.IsComplete) return false; // A partial table read cannot prove an endpoint closed.
            bool changed = false;
            HashSet<ApplicationView> recount = new HashSet<ApplicationView>();
            Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
            HashSet<string> currentKeys = new HashSet<string>();
            Dictionary<string, int> unknownDuplicates = new Dictionary<string, int>();
            foreach (var record in snapshot.Endpoints) {
                string key = DisplayEndpointKey(record, unknownDuplicates);
                currentKeys.Add(key);
                pending[key] = new Pending { Record = record, Seen = snapshot.CapturedAtUtc, Current = true };
            }
            if (observed != null)
            {
                foreach (var observation in observed)
                {
                    if (observation == null || observation.Record == null || observation.Record.Process.StartTimeUtcTicks <= 0 ||
                        observation.LastSeenUtc > snapshot.CapturedAtUtc || (snapshot.CapturedAtUtc - observation.LastSeenUtc).TotalSeconds > HistorySeconds) continue;
                    if (currentKeys.Contains(observation.Record.Key)) continue;
                    EndpointView existing;
                    if (endpoints.TryGetValue(observation.Record.Key, out existing) && existing.LastSeenUtc >= observation.LastSeenUtc) continue;
                    Pending previous;
                    if (pending.TryGetValue(observation.Record.Key, out previous) && previous.Seen >= observation.LastSeenUtc) continue;
                    pending[observation.Record.Key] = new Pending { Record = observation.Record, Seen = observation.LastSeenUtc };
                }
            }
            // Select one newest identity per process before touching any endpoint.
            Dictionary<string, Pending> identities = new Dictionary<string, Pending>();
            foreach (var item in pending.Values) {
                string key = DisplayProcessKey(item.Record.Process);
                Pending previous;
                if (!identities.TryGetValue(key, out previous) || item.Current && !previous.Current || item.Current == previous.Current && item.Seen > previous.Seen) identities[key] = item;
            }
            foreach (var identity in identities) changed |= ApplyIdentity(identity.Key, identity.Value, recount);
            foreach (var pair in pending) {
                var item = pair.Value;
                EndpointView endpoint;
                if (endpoints.TryGetValue(pair.Key, out endpoint)) {
                    if (endpoint.Update(item.Record, item.Seen, item.Current)) { changed = true; recount.Add(processOwners[DisplayProcessKey(item.Record.Process)]); }
                }
                else {
                    endpoint = new EndpointView(item.Record, item.Seen, pair.Key); endpoint.IsCurrent = item.Current;
                    endpoints.Add(pair.Key, endpoint); processes[DisplayProcessKey(item.Record.Process)].Endpoints.Add(endpoint); changed = true;
                    recount.Add(processOwners[DisplayProcessKey(item.Record.Process)]);
                }
            }
            foreach (var endpoint in endpoints.Values) {
                if (endpoint.IsCurrent && !currentKeys.Contains(endpoint.Key)) { endpoint.IsCurrent = false; changed = true; recount.Add(processOwners[DisplayProcessKey(endpoint.Record.Process)]); }
            }
            // 不可核验身份仅显示当前快照，避免猜测历史归属。
            foreach (var endpoint in endpoints.Values.Where(e => !e.IsCurrent && (e.Record.Process.StartTimeUtcTicks <= 0 ||
                (snapshot.CapturedAtUtc - e.LastSeenUtc).TotalSeconds > HistorySeconds)).ToList()) { recount.Add(processOwners[DisplayProcessKey(endpoint.Record.Process)]); RemoveEndpoint(endpoint); changed = true; }
            var history = endpoints.Values.Where(e => !e.IsCurrent).OrderByDescending(e => e.LastSeenUtc).ToList();
            foreach (var endpoint in history.Skip(HistoryLimit)) {
                DateTime expires = endpoint.LastSeenUtc.AddSeconds(HistorySeconds);
                if (expires > truncatedUntilUtc) truncatedUntilUtc = expires;
                recount.Add(processOwners[DisplayProcessKey(endpoint.Record.Process)]); RemoveEndpoint(endpoint); changed = true;
            }
            HistoryTruncated = truncatedUntilUtc != DateTime.MinValue && snapshot.CapturedAtUtc <= truncatedUntilUtc;
            foreach (var app in Applications.ToList()) {
                foreach (var process in app.Processes.Where(p => p.Endpoints.Count == 0).ToList()) { app.Processes.Remove(process); processes.Remove(process.Key); identityTimes.Remove(process.Key); processOwners.Remove(process.Key); changed = true; }
                if (app.Processes.Count == 0) { Applications.Remove(app); applications.Remove(app.Key); changed = true; } else if (recount.Contains(app)) changed |= app.Recount();
            }
            return changed;
        }
        private bool ApplyIdentity(string key, Pending item, HashSet<ApplicationView> recount)
        {
            bool changed = false;
            ProcessIdentity identity = item.Record.Process;
            ProcessView process;
            if (!processes.TryGetValue(key, out process)) {
                process = new ProcessView(identity, key); processes.Add(key, process); identityTimes[key] = item.Seen; changed = true;
            }
            else if (item.Current || identityTimes[key] <= item.Seen) {
                changed |= process.UpdateIdentity(identity); identityTimes[key] = item.Seen;
            }
            // Older historical records may have lacked path access. Use the newest
            // process identity so merging hidden history never moves it backwards.
            ApplicationView app;
            string appKey = process.Identity.StartTimeUtcTicks > 0 ? process.Identity.AppKey : key;
            if (!applications.TryGetValue(appKey, out app)) {
                app = new ApplicationView(process.Identity, appKey); applications.Add(app.Key, app); Applications.Add(app); changed = true;
            }
            else changed |= app.UpdateMetadata(process.Identity);
            ApplicationView owner;
            if (!processOwners.TryGetValue(key, out owner) || !Object.ReferenceEquals(owner, app)) {
                if (owner != null) { owner.Processes.Remove(process); recount.Add(owner); }
                app.Processes.Add(process); processOwners[key] = app; recount.Add(app); changed = true;
            }
            return changed;
        }
        private static string DisplayProcessKey(ProcessIdentity process)
        {
            // An unverified PID slot gives interaction continuity ONLY. It is not an
            // identity: raw InstanceId/endpoint keys retain their per-capture epoch,
            // and no absent unverified record is allowed into trusted history.
            return process.StartTimeUtcTicks > 0 ? process.InstanceId : "display-only:unverified-pid:" + process.ProcessId;
        }
        private static string DisplayEndpointKey(EndpointRecord record, Dictionary<string, int> duplicates)
        {
            if (record.Process.StartTimeUtcTicks > 0) return record.Key;
            string prefix = record.Process.InstanceId + "|";
            string tuple = record.Key != null && record.Key.StartsWith(prefix, StringComparison.Ordinal) ? record.Key.Substring(prefix.Length) :
                record.Protocol + "|" + record.AddressFamily + "|" + record.LocalAddress + ":" + record.LocalPort + "|" + record.RemoteAddress + ":" + record.RemotePort;
            string key = DisplayProcessKey(record.Process) + "|" + tuple;
            int count; duplicates.TryGetValue(key, out count); duplicates[key] = count + 1;
            return count == 0 ? key : key + "|duplicate:" + count;
        }
        private void RemoveEndpoint(EndpointView endpoint)
        {
            processes[DisplayProcessKey(endpoint.Record.Process)].Endpoints.Remove(endpoint); endpoints.Remove(endpoint.Key);
        }
    }

    public static class ExecutableIcons
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string path, int index, IntPtr[] large, IntPtr[] small, uint count);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        public static ImageSource Load(string path)
        {
            // Only local existing executable paths; never ask the shell to resolve a UNC target.
            if (String.IsNullOrEmpty(path) || path.StartsWith(@"\\")) return null;
            try { if (new DriveInfo(System.IO.Path.GetPathRoot(path)).DriveType != DriveType.Fixed || !File.Exists(path)) return null; } catch { return null; }
            IntPtr[] large = new IntPtr[1], small = new IntPtr[1];
            try {
                if (ExtractIconEx(path, 0, large, small, 1) == 0 || large[0] == IntPtr.Zero) return null;
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze(); return source;
            } catch { return null; }
            finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); if (small[0] != IntPtr.Zero && small[0] != large[0]) DestroyIcon(small[0]); }
        }
    }
}
