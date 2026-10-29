using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Web.Script.Serialization;
using NetPeek;
using NetPeek.Core;

// The same probe is compiled against each actual artifact; no product source is copied.
internal static class RefreshProbe
{
    private static readonly DateTime Epoch = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
    private static NetworkSnapshot Snapshot(int seconds, bool unknown, string epoch)
    {
        var snapshot = new NetworkSnapshot { IsComplete = true, CapturedAtUtc = Epoch.AddSeconds(seconds) };
        var identity = new ProcessIdentity {
            ProcessId = 4242, StartTimeUtcTicks = unknown ? 0 : Epoch.Ticks,
            InstanceId = unknown ? "unknown:4242:" + epoch : "4242:" + Epoch.Ticks,
            Name = unknown ? "未知进程 (PID 4242)" : "fixture", Path = unknown ? null : @"C:\fixtures\fixture.exe",
            AccessNote = unknown ? "身份不可核验，仅代表本次快照" : null
        };
        identity.AppKey = unknown ? "instance:" + identity.InstanceId : "path:" + identity.Path;
        for (int i = 0; i < (unknown ? 1 : 512); i++) snapshot.Endpoints.Add(new EndpointRecord {
            Key = identity.InstanceId + "|TCP|IPv4|127.0.0.1:" + (10000 + i) + "|127.0.0.1:45000",
            Protocol = "TCP", AddressFamily = "IPv4", LocalAddress = "127.0.0.1", LocalPort = 10000 + i,
            RemoteAddress = "127.0.0.1", RemotePort = 45000, State = "ESTABLISHED", Process = identity
        });
        return snapshot;
    }
    public static int Main()
    {
        var store = new ConnectionStore(); store.Apply(Snapshot(0, false, "one"));
        var app = store.Applications.Single(); var process = app.Processes.Single();
        int appEvents = 0, processEvents = 0, endpointEvents = 0, collectionEvents = 0;
        app.PropertyChanged += delegate { appEvents++; };
        process.PropertyChanged += delegate { processEvents++; };
        foreach (var endpoint in process.Endpoints) endpoint.PropertyChanged += delegate { endpointEvents++; };
        store.Applications.CollectionChanged += delegate { collectionEvents++; };
        app.Processes.CollectionChanged += delegate { collectionEvents++; };
        process.Endpoints.CollectionChanged += delegate { collectionEvents++; };
        store.Apply(Snapshot(2, false, "two"));
        bool timeCorrect = process.Endpoints.All(e => e.LastSeenUtc == Epoch.AddSeconds(2));

        var unknownStore = new ConnectionStore(); unknownStore.Apply(Snapshot(0, true, "one"));
        var unknownApp = unknownStore.Applications.Single(); var unknownProcess = unknownApp.Processes.Single();
        var unknownEndpoint = unknownProcess.Endpoints.Single(); string oldRawKey = unknownEndpoint.Record.Key;
        unknownApp.IsExpanded = true; unknownProcess.IsExpanded = true;
        unknownStore.Apply(Snapshot(2, true, "two"));
        var nextApp = unknownStore.Applications.Single(); var nextProcess = nextApp.Processes.Single();
        var nextEndpoint = nextProcess.Endpoints.Single();
        Console.WriteLine(new JavaScriptSerializer().Serialize(new {
            EndpointFixtureCount = 512, ApplicationNotifications = appEvents, ProcessNotifications = processEvents,
            EndpointNotifications = endpointEvents, CollectionNotifications = collectionEvents, LastSeenAccurate = timeCorrect,
            UnknownApplicationReused = Object.ReferenceEquals(unknownApp, nextApp),
            UnknownProcessReused = Object.ReferenceEquals(unknownProcess, nextProcess),
            UnknownEndpointReused = Object.ReferenceEquals(unknownEndpoint, nextEndpoint),
            UnknownExpansionRetained = nextApp.IsExpanded && nextProcess.IsExpanded,
            UnknownRawEpochStillDistinct = nextEndpoint.Record.Key != oldRawKey,
            UnknownRemainsUnverified = nextProcess.Identity.StartTimeUtcTicks == 0 && nextProcess.Note.Contains("身份不可核验")
        }));
        return 0;
    }
}
