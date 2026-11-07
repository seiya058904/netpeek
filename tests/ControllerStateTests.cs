using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
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
            FilterAndPause(window, controller, collection, render, epoch.AddMinutes(2));
        }
        finally {
            if (controller != null) ((IDisposable)controller).Dispose();
            if (window != null) window.Close();
        }
        Check(worker == null || !worker.IsAlive, "controller disposal leaves no collector thread; owned window and tray are closed");
        Console.WriteLine("SUCCESS " + passed + " final-EXE synthetic controller assertions; no shown window, Program.Main, mutex, sockets or performance measurement");
        return 0;
    }
    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T) yield return (T)child;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }
    private static void Arrange(Window window)
    {
        var root = (FrameworkElement)window.Content;
        window.Measure(new Size(1180, 800)); window.Arrange(new Rect(0, 0, 1180, 800));
        root.Measure(new Size(1172, 769)); root.Arrange(new Rect(0, 0, 1172, 769)); root.UpdateLayout(); Pump(20);
    }
    private static void Send(object collection, MethodInfo render, object controller, NetworkSnapshot snapshot)
    {
        ((CaptureHistory)Field(collection, "history")).Accept(snapshot); Publish(collection, snapshot); render.Invoke(controller, null);
    }
    private static void FilterAndPause(Window window, object controller, object collection, MethodInfo render, DateTime epoch)
    {
        var current = Enumerable.Range(0, 80).Select(i => Row("visible:" + i, 41000 + i, epoch)).ToArray();
        var gone = Row("historical", 42000, epoch); gone.RemoteAddress = "203.0.113.99";
        var udp = Row("udp", 43000, epoch); udp.Protocol = "UDP"; udp.State = "BOUND"; udp.RemoteAddress = null; udp.RemotePort = 0;
        Send(collection, render, controller, Snapshot(epoch, current.Concat(new[] { gone, udp }).ToArray()));
        Send(collection, render, controller, Snapshot(epoch.AddSeconds(1), current.Concat(new[] { udp }).ToArray()));
        var search = (TextBox)window.FindName("SearchBox"); var time = (ComboBox)window.FindName("TimeFilterBox"); var state = (ComboBox)window.FindName("FilterBox");
        var list = (ListBox)window.FindName("AppList");
        search.Text = "203.0.113.99"; Pump(200); state.SelectedIndex = 1; time.SelectedIndex = 1;
        Check(list.Items.Count == 0 && ((TextBlock)window.FindName("EmptyTitle")).Text.Contains("端点"), "actual search debounce plus current/state controls exclude an IP seen only in history");
        time.SelectedIndex = 2;
        var app = (ApplicationView)list.Items[0];
        Check(list.Items.Count == 1 && app.DisplayProcesses.Single().DisplayEndpoints.Single().Key == "historical" && app.DisplayStatus == "仅近期历史", "actual history control reveals a historical match within an active application");
        Check(((TextBlock)window.FindName("ResultText")).Text == "1 个匹配端点 · 1 个应用", "result summary counts matched endpoints rather than unrelated application totals");
        Arrange(window);
        var allButton = Visuals<Button>((DependencyObject)window.Content).Single(b => (string)b.Tag == "ShowAppEndpoints");
        allButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, allButton));
        Check(time.SelectedIndex == 0 && state.SelectedIndex == 0 && app.DisplayProcesses.Single().DisplayEndpoints.Count == 82 && !app.CanShowAll, "show-all action restores the selected application's full endpoint context through its actual button route");
        Click(window, "ResetFiltersButton");
        Check(search.Text == "" && time.SelectedIndex == 0 && state.SelectedIndex == 0 && app.DisplayProcesses.Single().DisplayEndpoints.Count == 82, "reset restores search, state and time together without deleting observations");
        search.Text = "TCP"; Pump(200); time.SelectedIndex = 1; Arrange(window);
        var grid = Visuals<DataGrid>((DependencyObject)window.Content).Single();
        Check(grid.Items.Count == 80 && grid.Items.Cast<EndpointView>().All(e => e.IsCurrent && e.Record.Protocol == "TCP"), "real DataGrid binds the filtered endpoint collection");
        var selected = (EndpointView)grid.Items[50]; grid.SelectedItem = selected; grid.ScrollIntoView(selected); Arrange(window);
        var scroll = Visuals<ScrollViewer>(grid).First(); scroll.ScrollToVerticalOffset(45); Arrange(window); double offset = scroll.VerticalOffset;
        Check(offset > 0, "scroll-retention fixture starts at an observed nonzero DataGrid offset");
        var addition = Row("new-udp", 44000, epoch); addition.Protocol = "UDP"; addition.State = "BOUND";
        Send(collection, render, controller, Snapshot(epoch.AddSeconds(2), current.Concat(new[] { udp, addition }).ToArray())); Arrange(window);
        Console.WriteLine("FILTERED GRID same=" + Object.ReferenceEquals(grid, Visuals<DataGrid>((DependencyObject)window.Content).Single()) + " selected=" + (grid.SelectedItem == selected) + " offset=" + offset + " -> " + scroll.VerticalOffset + " extent=" + scroll.ExtentHeight + " viewport=" + scroll.ViewportHeight);
        Check(Object.ReferenceEquals(grid, Visuals<DataGrid>((DependencyObject)window.Content).Single()) && grid.SelectedItem == selected && offset > 0 && Math.Abs(scroll.VerticalOffset - offset) < 0.01,
            "a changed snapshot retains the actual filtered DataGrid, selected endpoint and nonzero scroll offset");
        app.IsExpanded = false;
        Send(collection, render, controller, Snapshot(epoch.AddSeconds(3), current.Concat(new[] { udp }).ToArray()));
        Check(!app.IsExpanded, "background refresh does not reopen a result the user collapsed");
        Click(window, "PauseButton"); DateTime frozenTime = (DateTime)Field(controller, "lastSuccess");
        Send(collection, render, controller, Snapshot(epoch.AddSeconds(4)));
        search.Text = "UDP"; Pump(200); time.SelectedIndex = 1;
        Check((DateTime)Field(controller, "lastSuccess") == frozenTime && app.DisplayProcesses.Single().DisplayEndpoints.Single().Key == "udp", "search and time changes while paused still use the frozen displayed snapshot");
        Click(window, "PauseButton");
        Check(list.Items.Count == 0, "resume applies the latest empty snapshot and removes current matches");
        time.SelectedIndex = 2;
        Check(list.Items.Count == 1 && app.DisplayProcesses.Single().DisplayEndpoints.All(e => !e.IsCurrent), "resumed history matches remain explicitly historical");
        var udpView = app.DisplayProcesses.Single().DisplayEndpoints.First();
        var menu = (ContextMenu)controller.GetType().GetMethod("CreateCopyMenu", Private).Invoke(controller, new object[] { udpView });
        Check(((MenuItem)menu.Items[0]).IsEnabled && !((MenuItem)menu.Items[1]).IsEnabled && ((MenuItem)menu.Items[3]).Header.ToString() == "复制完整记录", "actual copy menu enables local/full copy and disables unavailable UDP remote copy");
        Click(window, "ClearSearchButton");
        Check(time.SelectedIndex == 0 && state.SelectedIndex == 0 && search.Text == "", "clear-search action also resets both endpoint selectors");
    }
}
