using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

// Actual Program.Main, native table sampling, rendered control routes and OS clipboard.
// Only test-owned loopback values are compared. Clipboard contents never enter reports.
internal static partial class WpfHarness
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    private static DataObject savedClipboard;
    private static bool clipboardPreserved;
    private static uint lastClipboardSequence;
    private static readonly List<IDisposable> clipboardSockets = new List<IDisposable>();
    private static TcpListener clipboardListener4, clipboardListener6;
    private static UdpClient clipboardUdp;
    private static int clipboardPort4, clipboardPort6, clipboardUdpPort;

    private static void PreserveClipboard()
    {
        var original = Clipboard.GetDataObject();
        savedClipboard = new DataObject();
        if (original != null) foreach (string format in original.GetFormats(false)) {
            Progress("materializing clipboard format"); object value = original.GetData(format, false);
            var stream = value as Stream;
            if (stream != null) {
                var copy = new MemoryStream(); long position = stream.CanSeek ? stream.Position : 0;
                if (stream.CanSeek) stream.Position = 0;
                stream.CopyTo(copy); copy.Position = 0;
                if (stream.CanSeek) stream.Position = position;
                value = copy;
            }
            if (value == null) throw new InvalidOperationException("Cannot preserve a clipboard format; no clipboard tests were started.");
            savedClipboard.SetData(format, value, false);
        }
        lastClipboardSequence = GetClipboardSequenceNumber(); clipboardPreserved = true;
    }
    private static void RestoreClipboard()
    {
        if (!clipboardPreserved) return;
        try {
            if (GetClipboardSequenceNumber() != lastClipboardSequence) {
                guiChecks.Add(new { Passed = (bool?)null, Name = "Clipboard restore skipped", Detail = "Clipboard changed outside the last verified test write; preserved the newer clipboard." });
                return;
            }
            for (int attempt = 0; attempt < 4; attempt++) {
                try { if (savedClipboard.GetFormats(false).Length == 0) Clipboard.Clear(); else Clipboard.SetDataObject(savedClipboard, true); }
                catch (COMException) { /* Clipboard managers may accept the data but block OLE flush. Verify the actual result. */ }
                if (ClipboardMatchesBackup()) {
                    clipboardPreserved = false;
                    Check(true, "Original clipboard restored", "Every materialized original format compared equal; contents stayed in memory only.");
                    return;
                }
                System.Threading.Thread.Sleep(50 * (attempt + 1));
            }
            throw new InvalidOperationException("Original clipboard formats did not compare equal after restoration.");
        } catch (Exception error) { failure = (failure == null ? "" : failure + Environment.NewLine) + "Clipboard restoration failed: " + error.GetType().Name + ": " + error.Message; }
    }
    private static bool ClipboardMatchesBackup()
    {
        var current = Clipboard.GetDataObject();
        foreach (string format in savedClipboard.GetFormats(false)) {
            if (current == null || !current.GetDataPresent(format, false)) { Progress("restore missing format: " + format); return false; }
            object expected = savedClipboard.GetData(format, false), actual = current.GetData(format, false);
            var expectedStream = expected as MemoryStream; var actualStream = actual as MemoryStream;
            if (expectedStream != null && actualStream != null) { if (!expectedStream.ToArray().SequenceEqual(actualStream.ToArray())) return false; }
            else if (expected is byte[] && actual is byte[]) { if (!((byte[])expected).SequenceEqual((byte[])actual)) return false; }
            else if (!Object.Equals(expected, actual)) { Progress("restore type mismatch: " + format + " " + expected.GetType().Name + "/" + (actual == null ? "null" : actual.GetType().Name)); return false; }
        }
        return savedClipboard.GetFormats(false).Length != 0 || current == null || current.GetFormats(false).Length == 0;
    }
    private static void StartClipboardFixtures()
    {
        fixturePid = Process.GetCurrentProcess().Id;
        clipboardListener4 = new TcpListener(IPAddress.Loopback, 0); clipboardListener4.Start();
        clipboardPort4 = ((IPEndPoint)clipboardListener4.LocalEndpoint).Port;
        var client4 = new TcpClient(); client4.Connect(IPAddress.Loopback, clipboardPort4);
        clipboardSockets.Add(client4); clipboardSockets.Add(clipboardListener4.AcceptTcpClient());
        clipboardListener6 = new TcpListener(IPAddress.IPv6Loopback, 0); clipboardListener6.Start();
        clipboardPort6 = ((IPEndPoint)clipboardListener6.LocalEndpoint).Port;
        var client6 = new TcpClient(AddressFamily.InterNetworkV6); client6.Connect(IPAddress.IPv6Loopback, clipboardPort6);
        clipboardSockets.Add(client6); clipboardSockets.Add(clipboardListener6.AcceptTcpClient());
        clipboardUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        clipboardUdpPort = ((IPEndPoint)clipboardUdp.Client.LocalEndPoint).Port;
    }
    private static void DisposeClipboardFixtures()
    {
        foreach (var socket in clipboardSockets) socket.Dispose();
        if (clipboardUdp != null) clipboardUdp.Close();
        if (clipboardListener4 != null) clipboardListener4.Stop();
        if (clipboardListener6 != null) clipboardListener6.Stop();
    }
    private static DataGrid ClipboardGrid()
    {
        return Visuals(window).OfType<DataGrid>().Single(g => g.IsVisible && g.DataContext != null &&
            Convert.ToInt32(Get(Get(g.DataContext, "Identity"), "ProcessId")) == fixturePid);
    }
    private static object ClipboardRow(string protocol, int localPort, string state)
    {
        return ClipboardGrid().Items.Cast<object>().Single(e => {
            object record = Get(e, "Record");
            return (string)Get(record, "Protocol") == protocol && Convert.ToInt32(Get(record, "LocalPort")) == localPort && (string)Get(record, "State") == state;
        });
    }
    private static DataGridRow RealizeClipboardRow(object endpoint)
    {
        var grid = ClipboardGrid(); grid.SelectedItem = endpoint; grid.ScrollIntoView(endpoint); grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(endpoint);
        if (row == null) throw new InvalidOperationException("Clipboard fixture row was not realized.");
        return row;
    }
    private static ContextMenu OpenClipboardMenu(object endpoint, bool rightClick)
    {
        var row = RealizeClipboardRow(endpoint);
        if (rightClick) {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) {
                RoutedEvent = Mouse.PreviewMouseUpEvent, Source = row
            };
            row.RaiseEvent(args);
        } else {
            var button = Visuals(row).OfType<Button>().Single(b => (string)b.Tag == "CopyMenu");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }
        var menu = PresentationSource.CurrentSources.Cast<PresentationSource>().Where(p => p.RootVisual != null)
            .SelectMany(p => Visuals(p.RootVisual)).OfType<ContextMenu>().SingleOrDefault(m => m.IsOpen);
        if (menu == null) throw new InvalidOperationException("The actual copy menu did not open.");
        return menu;
    }
    private static async Task CopyAndCheck(MenuItem item, string expected, string name)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        // Product clipboard retries are asynchronous and OLE flush can itself wait.
        // Wait for the observed result, not a fixed scheduler-dependent 350 ms.
        bool equal = false; DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        do {
            await Task.Delay(100);
            try { equal = Clipboard.GetText() == expected; } catch (COMException) { }
        } while (!equal && DateTime.UtcNow < deadline);
        if (equal) lastClipboardSequence = GetClipboardSequenceNumber();
        if (!equal) {
            string status = ((TextBlock)window.FindName("Footnote")).Text;
            guiChecks.Add(new { Passed = (bool?)null, Name = "Clipboard failure diagnostic", CopySuccessNotice = status.StartsWith("已复制"),
                CopyBusyNotice = status.Contains("剪贴板正被"), ClipboardChangedSinceLastVerifiedWrite = GetClipboardSequenceNumber() != lastClipboardSequence });
        }
        Check(equal, name, "Actual product menu action wrote the exact expected text to the Windows clipboard; values omitted.");
    }
    private static async void RunClipboard()
    {
        try {
            Progress("clipboard workflow ready"); PreserveClipboard(); Progress("clipboard preserved");
            ((TextBox)window.FindName("SearchBox")).Text = fixturePid.ToString();
            ((ComboBox)window.FindName("TimeFilterBox")).SelectedIndex = 1;
            await Task.Delay(500); window.UpdateLayout();
            Check(ClipboardGrid().Items.Count == 7, "Live owned fixture isolated", "IPv4/IPv6 TCP listeners and pairs plus one IPv4 UDP binding; exact PID search uses real collection.");
            var tcp4 = ClipboardRow("TCP", clipboardPort4, "ESTABLISHED");
            var tcp6 = ClipboardRow("TCP", clipboardPort6, "ESTABLISHED");
            foreach (var endpoint in new[] { tcp4, tcp6 }) {
                var record = Get(endpoint, "Record");
                bool ipv6 = (string)Get(record, "AddressFamily") == "IPv6";
                string local = (ipv6 ? "[::1]:" : "127.0.0.1:") + Get(record, "LocalPort");
                string remote = (ipv6 ? "[::1]:" : "127.0.0.1:") + Get(record, "RemotePort");
                var row = RealizeClipboardRow(endpoint);
                var quick = Visuals(row).OfType<Button>().Single(b => (string)b.Tag == "CopyEndpoint");
                quick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, quick)); await Task.Delay(350);
                bool equal = Clipboard.GetText() == remote; if (equal) lastClipboardSequence = GetClipboardSequenceNumber();
                Check(equal, (ipv6 ? "IPv6" : "IPv4") + " quick copy reaches clipboard", "Exact independent loopback endpoint expectation; IPv6 brackets retained.");
                var menu = OpenClipboardMenu(endpoint, false);
                await CopyAndCheck((MenuItem)menu.Items[0], local, "Menu local copy"); menu.IsOpen = false;
                menu = OpenClipboardMenu(endpoint, false);
                await CopyAndCheck((MenuItem)menu.Items[1], remote, "Menu remote copy"); menu.IsOpen = false;
                menu = OpenClipboardMenu(endpoint, false);
                string frozen = (string)Get(endpoint, "DetailsText"); DateTime observed = (DateTime)Get(endpoint, "LastSeenUtc");
                await Task.Delay(3200);
                Check((DateTime)Get(endpoint, "LastSeenUtc") > observed, "Sampling continues with copy menu open", "A newer real collector observation arrived before choosing full copy.");
                await CopyAndCheck((MenuItem)menu.Items[3], frozen, "Full copy freezes menu-open observation"); menu.IsOpen = false;
                Check(frozen.Contains(local) && frozen.Contains(remote) && frozen.Contains("PID：" + fixturePid) && frozen.Contains("采样时间：") && frozen.Contains("当前快照"),
                    "Full record carries provenance", "Independent endpoint, identity, current-state and timestamp checks; private text omitted.");
            }
            foreach (var endpoint in new[] { ClipboardRow("TCP", clipboardPort4, "LISTEN"), ClipboardRow("UDP", clipboardUdpPort, "BOUND") }) {
                var menu = OpenClipboardMenu(endpoint, false);
                Check(!((MenuItem)menu.Items[1]).IsEnabled, "Unavailable remote action disabled", "Real listener/UDP row cannot offer an invented remote endpoint.");
                await CopyAndCheck((MenuItem)menu.Items[0], "127.0.0.1:" + Get(Get(endpoint, "Record"), "LocalPort"), "Listener/UDP local copy"); menu.IsOpen = false;
            }
            var udp = ClipboardRow("UDP", clipboardUdpPort, "BOUND"); clipboardUdp.Close();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while ((bool)Get(udp, "IsCurrent") && DateTime.UtcNow < deadline) await Task.Delay(400);
            Check(!(bool)Get(udp, "IsCurrent"), "Closed UDP becomes history", "Real socket closed; native collector and time filter removed it from current results.");
            ((ComboBox)window.FindName("TimeFilterBox")).SelectedIndex = 2; await Task.Delay(400);
            Check(ClipboardGrid().Items.Count == 1, "History filter isolates closed binding", "Active TCP endpoints remain in the store but not this filtered grid.");
            var historyMenu = OpenClipboardMenu(udp, false); string historyText = (string)Get(udp, "DetailsText");
            await CopyAndCheck((MenuItem)historyMenu.Items[3], historyText, "Historical full record reaches clipboard"); historyMenu.IsOpen = false;
            Check(historyText.Contains("最后观察时间：") && historyText.Contains("不代表当前连接") && historyText.Contains("远端端点：未知"), "Historical copy remains explicit", "No current-connection claim or invented UDP remote.");
            // Exercise the right-click route independently of the explicit arrow.
            var rightMenu = OpenClipboardMenu(udp, true);
            await CopyAndCheck((MenuItem)rightMenu.Items[0], "127.0.0.1:" + clipboardUdpPort, "Right-click copy reaches clipboard"); rightMenu.IsOpen = false;
            Click("InfoButton"); var help = window.OwnedWindows.Cast<Window>().Single();
            Check(help.IsVisible && window.IsEnabled, "Help is nonmodal", "The actual owned help window is open without disabling the main window.");
            help.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(help), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!help.IsVisible && window.IsVisible, "Escape closes only help", "Actual detail-window key handler keeps main window alive.");
            Click("PauseButton"); Click("WarningDetailsButton");
            var warning = window.OwnedWindows.Cast<Window>().Single();
            Check(Visuals(warning).OfType<TextBox>().Single().Text.Contains(((TextBlock)window.FindName("WarningText")).Text), "Warning details contain the full message", "Paused snapshot warning opens as selectable full text.");
            warning.Close(); Click("PauseButton");
            RestoreClipboard(); completed = true; window.Close();
        } catch (Exception error) { Fail(error); }
    }
}
