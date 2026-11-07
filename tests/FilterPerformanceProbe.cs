using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Web.Script.Serialization;
using NetPeek;
using NetPeek.Core;

// Bounded synthetic display-projection workload, separate from live WPF CPU tests.
internal static class FilterPerformanceProbe
{
    public static int Main()
    {
        var now = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<EndpointRecord>();
        for (int app = 0; app < 40; app++) {
            var identity = new ProcessIdentity { ProcessId = 10000 + app, Name = "fixture" + app,
                Path = @"C:\NetPeekTest\fixture" + app + ".exe", StartTimeUtcTicks = now.AddMinutes(-1).Ticks,
                InstanceId = "fixture:" + app, AppKey = "path:fixture" + app };
            for (int row = 0; row < 400; row++) rows.Add(new EndpointRecord { Process = identity,
                Protocol = row % 4 == 0 ? "UDP" : "TCP", State = row % 4 == 0 ? "BOUND" : "ESTABLISHED", AddressFamily = "IPv4",
                LocalAddress = "127.0.0.1", LocalPort = 20000 + row, RemoteAddress = row % 4 == 0 ? null : "203.0.113.7", RemotePort = row % 4 == 0 ? 0 : 443,
                Key = app + ":" + row });
        }
        var store = new ConnectionStore();
        store.Apply(new NetworkSnapshot { IsComplete = true, CapturedAtUtc = now, Endpoints = rows });
        store.Apply(new NetworkSnapshot { IsComplete = true, CapturedAtUtc = now.AddSeconds(2), Endpoints = rows.Where((r, i) => i % 4 == 0).ToList() });
        var queries = new[] { new EndpointFilter("", 0, 0), new EndpointFilter("203.0.113.7", 1, 2), new EndpointFilter("UDP", 3, 1), new EndpointFilter("missing", 0, 0) };
        var expected = new[] { 16000, 12000, 4000, 0 };
        for (int warmup = 0; warmup < 3; warmup++) foreach (var query in queries) foreach (var app in store.Applications) query.Apply(app);
        var durations = new List<double>();
        for (int run = 0; run < 12; run++) for (int q = 0; q < queries.Length; q++) {
            var watch = Stopwatch.StartNew(); int count = store.Applications.Sum(a => queries[q].Apply(a)); watch.Stop();
            if (count != expected[q]) throw new InvalidOperationException("Filter count drift under load.");
            durations.Add(watch.Elapsed.TotalMilliseconds);
        }
        double[] ordered = durations.OrderBy(x => x).ToArray();
        double p95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
        Console.WriteLine(new JavaScriptSerializer().Serialize(new { Passed = p95 < 100, Applications = 40, Current = 4000, Historical = 12000,
            Samples = durations.Count, MedianMilliseconds = ordered[ordered.Length / 2], P95Milliseconds = p95, MaximumMilliseconds = ordered.Last(),
            GateP95Milliseconds = 100, Method = "Actual final EXE display projection over synthetic data; repeated all/history/current/empty queries. Excludes debounce, WPF rendering, native capture and mouse-to-display latency." }));
        return p95 < 100 ? 0 : 1;
    }
}
