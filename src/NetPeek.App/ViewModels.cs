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
        public string Key { get { return Record.Key; } }
        public DateTime LastSeenUtc { get; private set; }
        private bool current;
        public bool IsCurrent { get { return current; } set { if (Set(ref current, value, "IsCurrent")) { Changed("Status"); Changed("TimeText"); Changed("Opacity"); } } }
        public string Transport { get { return Record.Protocol + " / " + Record.AddressFamily; } }
        public string Local { get { return Format(Record.LocalAddress, Record.LocalPort); } }
        public string Remote { get { return Record.Protocol == "UDP" ? "未知 · 系统表不提供" : Record.State == "LISTEN" ? "—" : Format(Record.RemoteAddress, Record.RemotePort); } }
        public string Scope { get { return Record.Protocol == "UDP" || Record.State == "LISTEN" ? "本地端点" : AddressScope(Record.RemoteAddress); } }
        public string Status { get { return !IsCurrent ? "历史出现" : StateLabel(Record.State); } }
        public string TimeText { get { return LastSeenUtc.ToLocalTime().ToString("HH:mm:ss"); } }
        public double Opacity { get { return IsCurrent ? 1 : 0.62; } }
        public string CopyText { get { return Record.Protocol == "UDP" || Record.State == "LISTEN" ? Local : Remote; } }
        public EndpointView(EndpointRecord record, DateTime time) { Record = record; LastSeenUtc = time; current = true; }
        public void Update(EndpointRecord record, DateTime time)
        {
            bool changed = Record.State != record.State;
            Record = record; LastSeenUtc = time; IsCurrent = true;
            if (changed) { Changed("Status"); Changed("Remote"); Changed("CopyText"); }
            Changed("TimeText");
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
        public string Key { get { return Identity.InstanceId; } }
        public string Title { get { return "PID " + Identity.ProcessId + "  ·  " + Identity.Name; } }
        public string Note { get { return String.IsNullOrEmpty(Identity.AccessNote) ? "进程创建于 " + (Identity.StartTimeUtcTicks > 0 ? new DateTime(Identity.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss") : "未知") : Identity.AccessNote; } }
        private bool expanded;
        public bool IsExpanded { get { return expanded; } set { Set(ref expanded, value, "IsExpanded"); } }
        public ObservableCollection<EndpointView> Endpoints { get; private set; }
        public string CountText { get { return Endpoints.Count(x => x.IsCurrent) + " 个当前端点"; } }
        public ProcessView(ProcessIdentity identity) { Identity = identity; Endpoints = new ObservableCollection<EndpointView>(); }
        public void UpdateIdentity(ProcessIdentity identity) { Identity = identity; Changed("Title"); Changed("Note"); }
        public void NotifyCounts() { Changed("CountText"); }
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
        private int established, listening, udp, currentCount;
        public int Established { get { return established; } }
        public int CurrentCount { get { return currentCount; } }
        public string CountText { get { return currentCount.ToString(); } }
        public string CountLabel { get { return currentCount > 0 ? "当前端点" : "仅近期记录"; } }
        public string Subtitle { get { return Processes.Count + " 个进程" + (String.IsNullOrEmpty(Path) ? "  ·  归属信息受限" : "  ·  " + System.IO.Path.GetFileName(Path)); } }
        public string Status { get { return established > 0 ? "TCP 已建立" : currentCount == 0 ? "历史出现" : listening > 0 && udp == 0 && currentCount == listening ? "仅监听" : udp > 0 && currentCount == udp ? "UDP 本地端点" : "本地 / 其他状态"; } }
        public Brush StatusBrush { get { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(established > 0 ? "#E1F1E7" : currentCount == 0 ? "#EFF0ED" : "#EDF1F7")); } }
        public ObservableCollection<ProcessView> Processes { get; private set; }
        public ApplicationView(ProcessIdentity identity) { Key = identity.AppKey; Name = identity.Name; Path = identity.Path; Processes = new ObservableCollection<ProcessView>(); }
        public void Recount()
        {
            var endpoints = Processes.SelectMany(p => p.Endpoints).Where(e => e.IsCurrent).ToList();
            currentCount = endpoints.Count; established = endpoints.Count(e => e.Record.Protocol == "TCP" && e.Record.State == "ESTABLISHED");
            listening = endpoints.Count(e => e.Record.State == "LISTEN"); udp = endpoints.Count(e => e.Record.Protocol == "UDP");
            foreach (var p in Processes) p.NotifyCounts();
            foreach (string property in new[] { "CurrentCount", "CountText", "CountLabel", "Subtitle", "Status", "StatusBrush", "Established" }) Changed(property);
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
        public bool HistoryTruncated { get; private set; }
        public ConnectionStore() { Applications = new ObservableCollection<ApplicationView>(); }
        public void Apply(NetworkSnapshot snapshot)
        {
            Apply(snapshot, null);
        }
        public void Apply(NetworkSnapshot snapshot, IList<Observation> observed)
        {
            if (!snapshot.IsComplete) return; // A partial table read cannot prove an endpoint closed.
            foreach (var endpoint in endpoints.Values) endpoint.IsCurrent = false;
            if (observed != null)
            {
                foreach (var observation in observed)
                {
                    if (observation == null || observation.Record == null || observation.Record.Process.StartTimeUtcTicks <= 0 ||
                        observation.LastSeenUtc > snapshot.CapturedAtUtc || (snapshot.CapturedAtUtc - observation.LastSeenUtc).TotalSeconds > HistorySeconds) continue;
                    EndpointView existing;
                    if (endpoints.TryGetValue(observation.Record.Key, out existing) && existing.LastSeenUtc >= observation.LastSeenUtc) continue;
                    ApplyRecord(observation.Record, observation.LastSeenUtc, false);
                }
            }
            foreach (var record in snapshot.Endpoints) ApplyRecord(record, snapshot.CapturedAtUtc, true);
            // 不可核验身份仅显示当前快照，避免猜测历史归属。
            foreach (var endpoint in endpoints.Values.Where(e => !e.IsCurrent && (e.Record.Process.StartTimeUtcTicks <= 0 ||
                (snapshot.CapturedAtUtc - e.LastSeenUtc).TotalSeconds > HistorySeconds)).ToList()) RemoveEndpoint(endpoint);
            var history = endpoints.Values.Where(e => !e.IsCurrent).OrderByDescending(e => e.LastSeenUtc).ToList();
            HistoryTruncated = history.Count > HistoryLimit;
            foreach (var endpoint in history.Skip(HistoryLimit)) RemoveEndpoint(endpoint);
            foreach (var app in Applications.ToList()) {
                foreach (var process in app.Processes.Where(p => p.Endpoints.Count == 0).ToList()) { app.Processes.Remove(process); processes.Remove(process.Key); identityTimes.Remove(process.Key); }
                if (app.Processes.Count == 0) { Applications.Remove(app); applications.Remove(app.Key); } else app.Recount();
            }
        }
        private void ApplyRecord(EndpointRecord record, DateTime seen, bool current)
        {
            ProcessView process;
            if (!processes.TryGetValue(record.Process.InstanceId, out process)) {
                process = new ProcessView(record.Process); processes.Add(process.Key, process); identityTimes[process.Key] = seen;
            }
            else if (current || identityTimes[process.Key] <= seen) {
                process.UpdateIdentity(record.Process); identityTimes[process.Key] = seen;
            }
            // Older historical records may have lacked path access. Use the newest
            // process identity so merging hidden history never moves it backwards.
            ApplicationView app;
            if (!applications.TryGetValue(process.Identity.AppKey, out app)) {
                app = new ApplicationView(process.Identity); applications.Add(app.Key, app); Applications.Add(app);
            }
            if (!app.Processes.Contains(process)) {
                foreach (var previous in Applications) previous.Processes.Remove(process);
                app.Processes.Add(process);
            }
            EndpointView endpoint;
            if (endpoints.TryGetValue(record.Key, out endpoint)) endpoint.Update(record, seen);
            else { endpoint = new EndpointView(record, seen); endpoints.Add(record.Key, endpoint); process.Endpoints.Add(endpoint); }
            endpoint.IsCurrent = current;
        }
        private void RemoveEndpoint(EndpointView endpoint)
        {
            processes[endpoint.Record.Process.InstanceId].Endpoints.Remove(endpoint); endpoints.Remove(endpoint.Key);
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
