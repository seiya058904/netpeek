using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using NetPeek;
using NetPeek.Core;

// Synthetic, in-process controller regression. Never calls Program.Main, Show or a GUI message loop.
internal static class ControllerStateTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    private static object Field(object target, string name) { return target.GetType().GetField(name, Private).GetValue(target); }
    private static void Publish(object collection, NetworkSnapshot snapshot)
    {
        lock (Field(collection, "gate")) {
            collection.GetType().GetField("latest", Private).SetValue(collection, snapshot);
            long next = (long)Field(collection, "sequence") + 1;
            collection.GetType().GetField("sequence", Private).SetValue(collection, next);
        }
    }
    private static EndpointRecord Row(string key, int port, DateTime epoch)
    {
        return new EndpointRecord { Key = key, Protocol = "TCP", AddressFamily = "IPv4", LocalAddress = "127.0.0.1", LocalPort = port,
            RemoteAddress = "127.0.0.1", RemotePort = 443, State = "ESTABLISHED", Process = new ProcessIdentity { ProcessId = 7,
                StartTimeUtcTicks = epoch.Ticks, InstanceId = "synthetic:7:" + epoch.Ticks, AppKey = "instance:synthetic:7", Name = "Offline controller fixture", AccessNote = "Synthetic test identity; no process or socket created" } };
    }
    private static NetworkSnapshot Snapshot(DateTime time, params EndpointRecord[] rows) { return new NetworkSnapshot { CapturedAtUtc = time, IsComplete = true, Endpoints = rows.ToList() }; }
    public static int Main()
    {
        Window window = null; object controller = null; Thread worker = null;
        try {
            Assembly artifact = typeof(ConnectionStore).Assembly;
            using (var xaml = artifact.GetManifestResourceStream("NetPeek.MainWindow.xaml")) window = (Window)XamlReader.Load(xaml);
            Type controllerType = artifact.GetType("NetPeek.MainController", true);
            controller = Activator.CreateInstance(controllerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { window, Stopwatch.StartNew(), null, false, 0 }, null);
            object collection = Field(controller, "collection");
            ((IDisposable)collection).Dispose();
            worker = (Thread)Field(collection, "thread");
            Check(!worker.IsAlive && !window.IsVisible, "collector is stopped before synthetic publication; XAML window was never shown");
            CaptureHistory history = (CaptureHistory)Field(collection, "history");
            // Advance synthetic time to age out any initial native read performed by the constructor.
            DateTime epoch = DateTime.UtcNow.AddMinutes(2);
            history.Accept(Snapshot(epoch));
            MethodInfo render = controllerType.GetMethod("RenderLatest", Private);
            NetworkSnapshot baseline = Snapshot(epoch.AddSeconds(1), Row("baseline", 40000, epoch));
            history.Accept(baseline); Publish(collection, baseline); render.Invoke(controller, null);
            ConnectionStore store = (ConnectionStore)Field(controller, "store");
            EndpointView retained = store.Applications.Single().Processes.Single().Endpoints.Single();
            DateTime lastSuccess = (DateTime)Field(controller, "lastSuccess");
            int applied = (int)Field(controller, "samplesApplied");
            Check(retained.IsCurrent && retained.Key == "baseline" && applied == 1, "controller applies one known complete baseline snapshot");

            NetworkSnapshot busy = Snapshot(epoch.AddSeconds(2), Enumerable.Range(1, 12005).Select(i => Row("hidden:" + i, i, epoch)).ToArray());
            history.Accept(busy); history.Accept(Snapshot(epoch.AddSeconds(3)));
            Check(history.Truncated && history.Snapshot().Count == CaptureHistory.HistoryLimit, "hidden synthetic observations exceed history bound and genuinely set truncation");
            NetworkSnapshot partial = Snapshot(epoch.AddSeconds(4), Row("must-not-apply", 50000, epoch));
            partial.IsComplete = false; partial.Warnings.Add("Injected offline partial-table failure");
            history.Accept(partial); Publish(collection, partial); render.Invoke(controller, null);

            string warning = ((TextBlock)window.FindName("WarningText")).Text;
            Check(((Border)window.FindName("WarningPanel")).Visibility == Visibility.Visible && warning.Contains("采集不完整") && warning.Contains("12,000") && warning.Contains("上次完整采样"), "partial latest result displays collection failure and prior complete-sample truncation together");
            Check(Object.ReferenceEquals(retained, store.Applications.Single().Processes.Single().Endpoints.Single()) && retained.IsCurrent && retained.Key == "baseline", "partial data and unseen history never replace the retained complete UI snapshot");
            Check((int)Field(controller, "samplesApplied") == applied && (DateTime)Field(controller, "lastSuccess") == lastSuccess, "partial render does not advance applied-snapshot count or freshness timestamp");
        }
        finally {
            if (controller != null) ((IDisposable)controller).Dispose();
            if (window != null) window.Close();
        }
        Check(worker == null || !worker.IsAlive, "controller disposal leaves no collector thread; owned window and tray are closed");
        Console.WriteLine("SUCCESS " + passed + " final-EXE synthetic controller assertions; no shown window, Program.Main, mutex, sockets or performance measurement");
        return 0;
    }
}
