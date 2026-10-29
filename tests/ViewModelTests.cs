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
        try { StableAndGrouping(); MetadataRecovery(); PartialAndScope(); HiddenHistory(); Bounds(); UnknownIdentity(); Copies(); Console.WriteLine("SUCCESS " + passed + " offline assertions"); return 0; }
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
}
