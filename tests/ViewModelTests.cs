using System;
using System.Collections.Generic;
using System.Linq;
using NetPeek;
using NetPeek.Core;

internal static class ViewModelTests
{
    private static int passed;
    private static readonly DateTime Epoch = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    private static EndpointRecord Row(string key, int pid, string path)
    {
        return new EndpointRecord { Key = key, Protocol = "TCP", AddressFamily = "IPv4", LocalAddress = "127.0.0.1", LocalPort = 1234,
            RemoteAddress = "127.0.0.1", RemotePort = 4321, State = "ESTABLISHED",
            Process = new ProcessIdentity { ProcessId = pid, StartTimeUtcTicks = Epoch.Ticks, InstanceId = pid + ":" + Epoch.Ticks,
                AppKey = path == null ? "instance:" + pid : "path:" + path.ToUpperInvariant(), Path = path,
                Name = path == null ? "未知进程" : System.IO.Path.GetFileNameWithoutExtension(path), AccessNote = path == null ? "路径访问受限" : null } };
    }
    private static NetworkSnapshot Snap(int seconds, params EndpointRecord[] rows) { return new NetworkSnapshot { CapturedAtUtc = Epoch.AddSeconds(seconds), IsComplete = true, Endpoints = rows.ToList() }; }
    private static List<EndpointView> Endpoints(ConnectionStore store) { return store.Applications.SelectMany(a => a.Processes).SelectMany(p => p.Endpoints).ToList(); }
    public static int Main()
    {
        try { StableAndGrouping(); DeltaNotifications(); MetadataRecovery(); PartialAndScope(); HiddenHistory(); Bounds(); UnknownIdentity(); UnverifiedSlots(); Copies(); EndpointFiltering(); ExactQueries(); EndpointPresentation(); CopyDetails(); Console.WriteLine("SUCCESS " + passed + " offline assertions"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }
    private static void StableAndGrouping()
    {
        ConnectionStore store = new ConnectionStore();
        store.Apply(Snap(0, Row("a", 1, @"C:\One\app.exe"), Row("b", 2, @"c:\one\APP.exe"), Row("c", 3, @"C:\Two\app.exe")));
        Check(store.Applications.Count == 2 && store.Applications[0].Processes.Count == 2, "same normalized path groups processes; same name different path stays separate");
        ApplicationView app = store.Applications[0]; ProcessView process = app.Processes[0]; EndpointView endpoint = process.Endpoints[0];
        app.IsExpanded = true; process.IsExpanded = true;
        store.Apply(Snap(1, Row("a", 1, @"C:\One\app.exe"), Row("b", 2, @"C:\One\app.exe"), Row("c", 3, @"C:\Two\app.exe")));
        Check(Object.ReferenceEquals(store.Applications[0], app) && Object.ReferenceEquals(app.Processes[0], process) && Object.ReferenceEquals(process.Endpoints[0], endpoint), "application process and endpoint view identity retained");
        Check(app.IsExpanded && process.IsExpanded && endpoint.IsCurrent, "refresh retains expanded state");
    }
    private static void MetadataRecovery()
    {
        ConnectionStore store = new ConnectionStore();
        store.Apply(Snap(0, Row("a", 1, null)));
        ProcessView process = store.Applications[0].Processes[0]; EndpointView endpoint = process.Endpoints[0]; process.IsExpanded = true;
        store.Apply(Snap(10, Row("a", 1, @"C:\One\app.exe")));
        Check(store.Applications.Count == 1 && store.Applications[0].Path == @"C:\One\app.exe" && Object.ReferenceEquals(store.Applications[0].Processes[0], process), "path access recovery reparents the existing process");
        Check(Object.ReferenceEquals(process.Endpoints[0], endpoint) && process.IsExpanded, "path recovery preserves endpoint identity and process expansion");
        IList<Observation> old = new List<Observation> { new Observation { Record = Row("older", 1, null), LastSeenUtc = Epoch.AddSeconds(5) } };
        store.Apply(Snap(11), old);
        Check(store.Applications.Count == 1 && store.Applications[0].Path == @"C:\One\app.exe" && process.Identity.Path == @"C:\One\app.exe" && process.Endpoints.Count == 2, "older hidden history never overwrites newer process path metadata");
    }
    private static void DeltaNotifications()
    {
        ConnectionStore store = new ConnectionStore();
        store.Apply(Snap(0, Row("a", 1, @"C:\One\app.exe"), Row("b", 1, @"C:\One\app.exe")));
        ApplicationView app = store.Applications.Single(); ProcessView process = app.Processes.Single(); EndpointView endpoint = process.Endpoints[0];
        int notifications = 0, collectionChanges = 0;
        app.PropertyChanged += delegate { notifications++; }; process.PropertyChanged += delegate { notifications++; };
        foreach (EndpointView view in process.Endpoints) view.PropertyChanged += delegate { notifications++; };
        store.Applications.CollectionChanged += delegate { collectionChanges++; }; app.Processes.CollectionChanged += delegate { collectionChanges++; }; process.Endpoints.CollectionChanged += delegate { collectionChanges++; };
        object brush = app.StatusBrush;
        bool changed = store.Apply(Snap(20, Row("a", 1, @"C:\One\app.exe"), Row("b", 1, @"C:\One\app.exe")));
        Check(!changed && notifications == 0 && collectionChanges == 0, "unchanged live sample raises zero property or collection notifications and no filter refresh");
        Check(endpoint.LastSeenUtc == Epoch.AddSeconds(20) && endpoint.TimeText == "本次快照", "silent current timestamp remains accurate for subsequent history");
        Check(Object.ReferenceEquals(brush, app.StatusBrush) && app.StatusBrush.IsFrozen, "status brush is frozen and reused across reads and unchanged samples");
        Dictionary<string, int> endpointEvents = new Dictionary<string, int>();
        endpoint.PropertyChanged += delegate(object sender, System.ComponentModel.PropertyChangedEventArgs args) { int count; endpointEvents.TryGetValue(args.PropertyName, out count); endpointEvents[args.PropertyName] = count + 1; };
        EndpointRecord closing = Row("a", 1, @"C:\One\app.exe"); closing.State = "CLOSE_WAIT";
        Check(store.Apply(Snap(21, closing, Row("b", 1, @"C:\One\app.exe"))) && endpointEvents.Count == 2 && endpointEvents.ContainsKey("Status") && endpointEvents.ContainsKey("StatusHint"), "state transition only notifies changed status text and explanation");
        int titleEvents = 0, noteEvents = 0;
        process.PropertyChanged += delegate(object sender, System.ComponentModel.PropertyChangedEventArgs args) { if (args.PropertyName == "Title") titleEvents++; if (args.PropertyName == "Note") noteEvents++; };
        EndpointRecord[] sameProcess = Enumerable.Range(0, 100).Select(i => Row("many" + i, 1, @"C:\One\app.exe")).ToArray();
        foreach (EndpointRecord row in sameProcess) { row.Process.Name = "renamed"; row.Process.AccessNote = "new note"; }
        store.Apply(Snap(22, sameProcess));
        Check(titleEvents == 1 && noteEvents == 1, "one process identity notification pass for a hundred endpoints");
        Check(endpoint.LastSeenUtc == Epoch.AddSeconds(21) && !endpoint.IsCurrent && endpoint.TimeText == Epoch.AddSeconds(21).ToLocalTime().ToString("HH:mm:ss"), "transition to history displays last real observation rather than closure time");
        notifications = 0; collectionChanges = 0;
        Check(!store.Apply(Snap(23, sameProcess)) && notifications == 0 && collectionChanges == 0, "mixed stable current and historical sample remains notification-free");
    }
    private static void PartialAndScope()
    {
        ConnectionStore store = new ConnectionStore(); CaptureHistory history = new CaptureHistory();
        NetworkSnapshot initial = Snap(0, Row("a", 1, @"C:\One\app.exe")); store.Apply(initial); history.Accept(initial);
        NetworkSnapshot partial = Snap(70); partial.IsComplete = false; partial.Warnings.Add("injected partial read");
        store.Apply(partial, new List<Observation>()); history.Accept(partial);
        Check(Endpoints(store).Single().IsCurrent && history.Snapshot().Single().LastSeenUtc == Epoch, "partial failure cannot close or expire existing observations");
        Check(EndpointView.AddressScope("::ffff:127.0.0.1") == "本机回环" && EndpointView.AddressScope("::1") == "本机回环", "IPv4-mapped IPv6 loopback remains local");
        Check(EndpointView.AddressScope("198.18.0.36") == "基准测试地址" && EndpointView.AddressScope("198.19.255.254") == "基准测试地址" && EndpointView.AddressScope("203.0.113.8") == "文档示例地址", "special-use and proxy test-range addresses are not labelled ordinary external destinations");
    }
    private static void HiddenHistory()
    {
        CaptureHistory history = new CaptureHistory(); ConnectionStore store = new ConnectionStore();
        history.Accept(Snap(0)); store.Apply(Snap(0));
        history.Accept(Snap(2, Row("brief", 1, @"C:\One\app.exe")));
        history.Accept(Snap(3));
        store.Apply(Snap(4), history.Snapshot());
        Check(Endpoints(store).Count == 1 && !Endpoints(store)[0].IsCurrent && Endpoints(store)[0].LastSeenUtc == Epoch.AddSeconds(2), "connection born and gone while UI hidden survives as timestamped history");
        history.Accept(Snap(55, Row("brief", 1, @"C:\One\app.exe"))); history.Accept(Snap(56));
        store.Apply(Snap(70), history.Snapshot());
        Check(Endpoints(store).Count == 1 && Endpoints(store)[0].LastSeenUtc == Epoch.AddSeconds(55) && !Endpoints(store)[0].IsCurrent, "hidden recapture refreshes last-seen without claiming current connection");
        history.Accept(Snap(115)); store.Apply(Snap(115), history.Snapshot());
        Check(history.Snapshot().Count == 1 && Endpoints(store).Count == 1, "history retained through exact 60-second boundary");
        history.Accept(Snap(116)); store.Apply(Snap(116), history.Snapshot());
        Check(history.Snapshot().Count == 0 && store.Applications.Count == 0, "history older than 60 seconds expires and empty groups are removed");
    }
    private static void Bounds()
    {
        EndpointRecord[] many = Enumerable.Range(0, 12005).Select(i => Row("row" + i, 1, @"C:\One\app.exe")).ToArray();
        CaptureHistory history = new CaptureHistory(); ConnectionStore store = new ConnectionStore();
        NetworkSnapshot busy = Snap(0, many); history.Accept(busy); store.Apply(busy);
        Check(history.Snapshot().Count == 12005 && Endpoints(store).Count == 12005 && !history.Truncated && !store.HistoryTruncated, "current connections are not capped by historical limit");
        NetworkSnapshot gone = Snap(1); history.Accept(gone); store.Apply(gone);
        Check(history.Snapshot().Count == 12000 && Endpoints(store).Count == 12000 && history.Truncated && store.HistoryTruncated, "historical records bounded at 12000 with truncation reported");
        history.Accept(Snap(2)); store.Apply(Snap(2));
        Check(history.Truncated && store.HistoryTruncated, "history truncation remains visible on subsequent under-limit samples");
        NetworkSnapshot failed = Snap(3); failed.IsComplete = false; history.Accept(failed); store.Apply(failed);
        Check(history.Truncated && store.HistoryTruncated, "incomplete sample cannot clear an existing truncation warning");
        history.Accept(Snap(60)); store.Apply(Snap(60));
        Check(history.Truncated && store.HistoryTruncated, "truncation warning includes the lost record's exact retention boundary");
        history.Accept(Snap(62)); store.Apply(Snap(62));
        Check(history.Snapshot().Count == 0 && Endpoints(store).Count == 0 && !history.Truncated && !store.HistoryTruncated, "expired history does not report capacity truncation");
    }
    private static void UnknownIdentity()
    {
        EndpointRecord unknown = Row("unknown", 9, null); unknown.Process.StartTimeUtcTicks = 0;
        CaptureHistory history = new CaptureHistory(); ConnectionStore store = new ConnectionStore();
        NetworkSnapshot initial = Snap(0, unknown, Row("verified-no-path", 10, null)); history.Accept(initial); store.Apply(initial);
        Check(history.Snapshot().Count == 2 && Endpoints(store).Count == 2, "unknown identity still appears in current snapshot");
        history.Accept(Snap(1)); store.Apply(Snap(1), history.Snapshot());
        Check(history.Snapshot().Single().Record.Key == "verified-no-path" && Endpoints(store).Single().Key == "verified-no-path", "unverifiable identity is never retained as history; verified inaccessible path is retained");
    }
    private static void Copies()
    {
        CaptureHistory history = new CaptureHistory(); EndpointRecord source = Row("a", 1, @"C:\One\app.exe"); history.Accept(Snap(0, source));
        source.State = "CLOSED"; source.Process.Name = "mutated";
        List<Observation> copy = history.Snapshot();
        Check(copy[0].Record.State == "ESTABLISHED" && copy[0].Record.Process.Name == "app", "capture history owns a deep copy of incoming mutable records");
        copy[0].Record.State = "CLOSED"; copy[0].Record.Process.Name = "mutated"; copy[0].LastSeenUtc = Epoch.AddYears(1); copy.Clear();
        Observation retained = history.Snapshot().Single();
        Check(retained.Record.State == "ESTABLISHED" && retained.Record.Process.Name == "app" && retained.LastSeenUtc == Epoch, "returned history snapshots cannot mutate retained state");
    }
    private static void EndpointFiltering()
    {
        var tcp = Row("tcp", 56789, @"C:\One\app.exe"); tcp.RemoteAddress = "203.0.113.10";
        var udp = Row("udp", 56789, @"C:\One\app.exe"); udp.Protocol = "UDP"; udp.State = "BOUND"; udp.RemoteAddress = null; udp.RemotePort = 0;
        var gone = Row("gone", 56789, @"C:\One\app.exe"); gone.RemoteAddress = "203.0.113.99";
        var listen = Row("listen", 567890, @"C:\One\app.exe"); listen.State = "LISTEN"; listen.RemoteAddress = null; listen.RemotePort = 0;
        var store = new ConnectionStore(); store.Apply(Snap(0, tcp, udp, gone, listen)); store.Apply(Snap(2, tcp, udp, listen));
        var app = store.Applications.Single(); var process = app.Processes[0]; var retained = process.Endpoints.Single(e => e.Key == "udp");
        Check(new EndpointFilter(" UDP ", 0, 0).Apply(app) == 1 && app.DisplayProcesses.Count == 1 && process.DisplayEndpoints.Single() == retained, "protocol query narrows the actual displayed process and endpoint collections");
        Check(process.Endpoints.Count == 3 && app.Processes.Count == 2, "display filters never delete source observations or process groups");
        Check(new EndpointFilter("203.0.113.99", 1, 1).Apply(app) == 0, "a historical IP cannot match current established TCP through a different endpoint");
        Check(new EndpointFilter("", 0, 2).Apply(app) == 1 && process.DisplayEndpoints.Single().Key == "gone" && app.DisplayStatus == "仅近期历史", "history remains discoverable inside an application that also has current endpoints");
        Check(new EndpointFilter("203.0.113.99", 1, 2).Apply(app) == 1 && !process.DisplayEndpoints.Single().IsCurrent, "history state filtering uses the last observed state without claiming it is current");
        Check(new EndpointFilter("APP", 3, 1).Apply(app) == 1 && process.DisplayEndpoints.Single() == retained, "application metadata matches still obey protocol and time filters on the same endpoint");
        Check(new EndpointFilter(@"C:\One\app.exe", 0, 0).Apply(app) == 4 && !app.CanShowAll, "full application path reveals all its endpoints and removes redundant show-all action");
        Check(new EndpointFilter("56789", 0, 0).Apply(app) == 3 && app.DisplayProcesses.Single() == process, "PID matching selects the exact process without selecting a longer PID");
        Check(new EndpointFilter("", 2, 1).Apply(app) == 1 && app.DisplayProcesses.Single().DisplayEndpoints.Single().Key == "listen", "listeners are independently searchable even when another process has established TCP");
        var query = new EndpointFilter("UDP", 0, 1); query.Apply(app); app.IsExpanded = true; process.IsExpanded = true;
        int changes = 0; app.DisplayProcesses.CollectionChanged += delegate { changes++; }; process.DisplayEndpoints.CollectionChanged += delegate { changes++; };
        store.Apply(Snap(3, tcp, udp, listen)); query.Apply(app);
        Check(changes == 0 && app.IsExpanded && process.IsExpanded && process.DisplayEndpoints.Single() == retained, "unchanged samples preserve filtered collection identity, expansion and endpoint objects without Reset");
        store.Apply(Snap(4, tcp, listen));
        Check(query.Apply(app) == 0 && new EndpointFilter("UDP", 0, 2).Apply(app) == 1, "a disappearing endpoint moves from current to history in filtered views");
        store.Apply(Snap(65, tcp, listen));
        Check(new EndpointFilter("", 0, 2).Apply(app) == 0 && new EndpointFilter("", 0, 0).Apply(app) == 2, "expiry removes historical matches and clearing filters restores surviving source endpoints");
    }
    private static void CopyDetails()
    {
        var row = Row("ipv6", 42, @"C:\One\app.exe"); row.AddressFamily = "IPv6"; row.LocalAddress = "fe80::1%12"; row.RemoteAddress = "fe80::2%12";
        var endpoint = new EndpointView(row, Epoch);
        Check(endpoint.Local == "[fe80::1%12]:1234" && endpoint.Remote == "[fe80::2%12]:4321" && endpoint.CopyText == endpoint.Remote && endpoint.CanCopyRemote, "TCP copy preserves IPv6 brackets, scope and the original default remote action");
        string details = endpoint.DetailsText;
        Check(details.Contains("PID：42") && details.Contains(row.Process.Path) && details.Contains(endpoint.Local) && details.Contains(endpoint.Remote) && details.Contains("当前快照") && details.Contains("采样时间：") && details.Contains("2026-10-29"), "complete current record includes both endpoints, identity and dated observation time");
        endpoint.IsCurrent = false;
        Check(endpoint.DetailsText.Contains("最后观察状态：已建立") && endpoint.DetailsText.Contains("近期历史（不代表当前连接）") && endpoint.DetailsText.Contains("最后观察时间：") && !endpoint.DetailsText.Contains("采样时间："), "historical copies describe last observation rather than an exact closure or current connection");
        row = Row("udp", 42, null); row.Protocol = "UDP"; row.State = "BOUND"; row.RemoteAddress = null; row.RemotePort = 0;
        endpoint = new EndpointView(row, Epoch);
        Check(!endpoint.CanCopyRemote && endpoint.CopyText == endpoint.Local && endpoint.DetailsText.Contains("远端端点：未知 · 系统表不提供"), "UDP copy never invents a remote endpoint");
        row = Row("listen", 42, null); row.State = "LISTEN"; row.RemoteAddress = null; row.RemotePort = 0;
        endpoint = new EndpointView(row, Epoch);
        Check(!endpoint.CanCopyRemote && endpoint.CopyText == endpoint.Local && endpoint.DetailsText.Contains("远端端点：—"), "listener copy explicitly has no remote endpoint");
        row = Unverified("copy"); endpoint = new EndpointView(row, Epoch);
        Check(endpoint.DetailsText.Contains("身份不可核验") && endpoint.DetailsText.Contains("路径：未知"), "complete copies retain unknown attribution instead of suggesting verified ownership");
    }
    private static string[] MatchKeys(ConnectionStore store, string query, int state = 0, int time = 0)
    {
        var filter = new EndpointFilter(query, state, time);
        foreach (var app in store.Applications) filter.Apply(app);
        return store.Applications.SelectMany(a => a.DisplayProcesses).SelectMany(p => p.DisplayEndpoints).Select(e => e.Key).OrderBy(k => k).ToArray();
    }
    private static void ExactQueries()
    {
        var pid = Row("pid", 443, @"C:\One\app.exe"); pid.LocalPort = 50100; pid.RemotePort = 80;
        var remote = Row("remote", 9001, @"C:\Two\app.exe"); remote.RemotePort = 443;
        var substring = Row("substring", 9002, @"C:\Three\app.exe"); substring.LocalPort = 54430; substring.RemotePort = 80;
        var local = Row("local", 9003, @"C:\Four\app.exe"); local.LocalPort = 443; local.RemotePort = 80;
        var udp = Row("udp", 9004, @"C:\Five\app.exe"); udp.Protocol = "UDP"; udp.State = "BOUND"; udp.RemoteAddress = null; udp.RemotePort = 0;
        var listen = Row("listen", 9005, @"C:\Six\app.exe"); listen.State = "LISTEN"; listen.RemoteAddress = null; listen.RemotePort = 0;
        var gone = Row("gone", 9006, @"C:\Seven\app.exe"); gone.RemotePort = 443; gone.State = "SYN_SENT";
        var ipv6 = Row("ipv6", 9007, @"C:\Eight\app.exe"); ipv6.AddressFamily = "IPv6"; ipv6.RemoteAddress = "fe80::443%12"; ipv6.RemotePort = 443;
        var store = new ConnectionStore(); store.Apply(Snap(0, pid, remote, substring, local, udp, listen, gone, ipv6));
        store.Apply(Snap(1, pid, remote, substring, local, udp, listen, ipv6));
        Check(MatchKeys(store, "443").Length == 6, "bare numeric search retains existing fuzzy matches across PID, ports and addresses");
        Check(MatchKeys(store, "pid:443").SequenceEqual(new[] { "pid" }), "explicit PID excludes matching ports and other process metadata");
        Check(MatchKeys(store, "port:443").SequenceEqual(new[] { "gone", "ipv6", "local", "remote" }), "explicit port matches exactly either endpoint side, including IPv6 and historical observations");
        Check(MatchKeys(store, " PID: 00443 ").SequenceEqual(new[] { "pid" }) && MatchKeys(store, "PORT:443").Length == 4, "exact queries accept case variants, surrounding spaces and leading zeros");
        Check(MatchKeys(store, "port:443", 1, 1).SequenceEqual(new[] { "ipv6", "local", "remote" }) && MatchKeys(store, "port:443", 4, 2).SequenceEqual(new[] { "gone" }), "exact port, state and time intersect on the same endpoint");
        Check(MatchKeys(store, "pid:9006", 0, 1).Length == 0 && MatchKeys(store, "pid:9006", 0, 2).Single() == "gone" && MatchKeys(store, "pid:443", 3).Length == 0, "exact PID respects current/history and transport filters");
        Check(MatchKeys(store, "port:0").Length == 0, "unavailable UDP and listener remote ports never match their placeholder zero");
        Check(MatchKeys(store, "port:1234", 3).Single() == "udp" && MatchKeys(store, "port:1234", 2).Single() == "listen", "UDP and listener local ports remain searchable");
        foreach (string invalid in new[] { "pid:", "pid:-1", "pid:+443", "pid:2147483648", "pid:4.43", "port:", "port:65536", "port:-1", "port:abc", "port:443 pid:443" })
            Check(new EndpointFilter(invalid, 0, 0).Error != null && MatchKeys(store, invalid).Length == 0, "invalid exact query has an explanation and no fuzzy fallback: " + invalid);
        var bound = Row("upper-bound", Int32.MaxValue, @"C:\Bound\app.exe"); bound.LocalPort = 65535;
        var zero = Row("zero", 0, null); zero.LocalPort = 0;
        var bounds = new ConnectionStore(); bounds.Apply(Snap(0, bound, zero));
        Check(MatchKeys(bounds, "pid:2147483647").Single() == "upper-bound" && MatchKeys(bounds, "port:65535").Single() == "upper-bound", "maximum PID and port values are valid");
        Check(MatchKeys(bounds, "pid:0").Single() == "zero" && MatchKeys(bounds, "port:0").Single() == "zero", "real zero PID/local port values remain inspectable");
        Check(MatchKeys(store, @"C:\Three\app.exe").Single() == "substring", "ordinary absolute path searches are not parsed as exact-query syntax");
    }
    private static void EndpointPresentation()
    {
        var tcp = Row("display", 42, @"C:\One\app.exe"); tcp.AddressFamily = "IPv6"; tcp.RemoteAddress = "fe80::2%12";
        var endpoint = new EndpointView(tcp, Epoch);
        Check(endpoint.PrimaryAddress == "[fe80::2%12]" && endpoint.PrimaryPort == ":4321" && endpoint.PrimaryRole == "远端" && endpoint.PrimaryAddress + endpoint.PrimaryPort == endpoint.CopyText, "primary TCP endpoint preserves IPv6 scope/brackets and agrees with quick copy");
        var events = new HashSet<string>(); endpoint.PropertyChanged += (sender, args) => events.Add(args.PropertyName);
        var udp = Row("display", 42, @"C:\One\app.exe"); udp.Protocol = "UDP"; udp.State = "BOUND"; udp.RemoteAddress = null; udp.RemotePort = 0;
        endpoint.Update(udp, Epoch.AddSeconds(1));
        Check(endpoint.PrimaryAddress == "127.0.0.1" && endpoint.PrimaryPort == ":1234" && endpoint.PrimaryRole.Contains("远端未知") && endpoint.PrimaryHint.Contains("系统表不提供") && !endpoint.CanCopyRemote, "UDP foregrounds local binding while explicitly retaining unavailable remote semantics");
        Check(new[] { "PrimaryAddress", "PrimaryPort", "PrimaryRole", "PrimaryHint" }.All(events.Contains), "reused rows notify every changed primary-endpoint binding");
        events.Clear(); endpoint.Update(udp, Epoch.AddSeconds(2));
        Check(events.Count == 0, "unchanged primary endpoint incurs no refresh notifications");
        var listen = Row("display", 42, null); listen.State = "LISTEN"; listen.LocalAddress = "::"; listen.LocalPort = 8080; listen.RemoteAddress = null; listen.RemotePort = 0;
        endpoint.Update(listen, Epoch.AddSeconds(3));
        Check(endpoint.PrimaryAddress == "[::]" && endpoint.PrimaryPort == ":8080" && endpoint.PrimaryRole == "本地 · 监听" && endpoint.CopyText == "[::]:8080" && !endpoint.CanCopyRemote, "listeners foreground their local wildcard endpoint without suggesting a remote peer");
        var pending = Row("history", 42, null); pending.State = "SYN_SENT";
        endpoint = new EndpointView(pending, Epoch); endpoint.IsCurrent = false;
        Check(endpoint.Status == "曾：正在连接" && endpoint.StatusHint.Contains("不代表当前连接") && endpoint.DetailsText.Contains("最后观察状态：正在连接"), "historical table state agrees with complete copy while clearly marking the past observation");
        events.Clear(); endpoint.PropertyChanged += (sender, args) => events.Add(args.PropertyName); endpoint.Update(pending, Epoch.AddSeconds(4));
        Check(endpoint.Status == "正在连接" && endpoint.StatusHint.StartsWith("当前快照") && events.Contains("StatusHint"), "returning to a current snapshot updates historical status and explanation together");
        var unknown = Row("unknown-address", 42, null); unknown.RemoteAddress = null;
        endpoint.Update(unknown, Epoch);
        Check(endpoint.PrimaryAddress == "未知" && endpoint.PrimaryPort == "" && endpoint.CopyText == "未知", "missing remote address does not invent a display port");
    }
    private static EndpointRecord Unverified(string epoch)
    {
        EndpointRecord record = Row("", 9, null); record.Process.StartTimeUtcTicks = 0;
        record.Process.InstanceId = "unknown:9:" + epoch; record.Process.AppKey = "instance:" + record.Process.InstanceId;
        record.Key = record.Process.InstanceId + "|TCP|IPv4|127.0.0.1:1234|127.0.0.1:4321";
        return record;
    }
    private static void UnverifiedSlots()
    {
        ConnectionStore store = new ConnectionStore(); CaptureHistory history = new CaptureHistory();
        NetworkSnapshot first = Snap(0, Unverified("one")); history.Accept(first); store.Apply(first);
        ApplicationView app = store.Applications.Single(); ProcessView process = app.Processes.Single(); EndpointView endpoint = process.Endpoints.Single();
        app.IsExpanded = true; process.IsExpanded = true;
        string rawKey = endpoint.Record.Key;
        int notifications = 0; app.PropertyChanged += delegate { notifications++; }; process.PropertyChanged += delegate { notifications++; }; endpoint.PropertyChanged += delegate { notifications++; };
        NetworkSnapshot next = Snap(1, Unverified("two")); history.Accept(next);
        Check(!store.Apply(next, history.Snapshot()) && notifications == 0 && Object.ReferenceEquals(app, store.Applications.Single()) && Object.ReferenceEquals(process, app.Processes.Single()) && Object.ReferenceEquals(endpoint, process.Endpoints.Single()), "unverified display slots reuse containers without notifications across epochs");
        Check(app.IsExpanded && process.IsExpanded && endpoint.Record.Key != rawKey && process.Identity.InstanceId == "unknown:9:two" && process.Note.Contains("身份不可核验"), "display continuity retains interaction only while raw identity and explicit uncertainty stay truthful");
        Check(history.Snapshot().Count == 1 && history.Snapshot()[0].Record.Process.InstanceId == "unknown:9:two", "background history never accumulates unknown epochs");
        NetworkSnapshot known = Snap(2, Row("verified", 9, @"C:\One\app.exe")); history.Accept(known); store.Apply(known, history.Snapshot());
        Check(store.Applications.Count == 1 && !Object.ReferenceEquals(store.Applications[0], app) && Endpoints(store).Single().Key == "verified", "becoming verified creates a separate trusted identity with no inherited unknown history");
        NetworkSnapshot unknownAgain = Snap(3, Unverified("three")); history.Accept(unknownAgain); store.Apply(unknownAgain, history.Snapshot());
        List<EndpointView> all = Endpoints(store);
        Check(all.Count == 2 && all.Single(e => e.Key == "verified").IsCurrent == false && all.Single(e => e.IsCurrent).Record.Process.StartTimeUtcTicks == 0, "losing verification keeps earlier trusted history separate from the unknown current slot");
        history.Accept(Snap(4)); store.Apply(Snap(4), history.Snapshot());
        Check(Endpoints(store).Count == 1 && Endpoints(store)[0].Key == "verified", "absent unknown slot is discarded instead of becoming trusted history");
    }
}
