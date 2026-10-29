using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NetPeek.Core;
using Forms = System.Windows.Forms;

namespace NetPeek
{
    internal sealed class MainController : IDisposable
    {
        private readonly Window window;
        private readonly Stopwatch startup;
        private readonly string metricsPath;
        private readonly ConnectionStore store = new ConnectionStore();
        private readonly CollectionLoop collection;
        private readonly DispatcherTimer renderTimer;
        private readonly DispatcherTimer searchTimer;
        private readonly ObservableCollection<ApplicationView> visibleApps = new ObservableCollection<ApplicationView>();
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ToolStripMenuItem trayPause;
        private readonly System.Drawing.Icon trayIcon;
        private readonly HashSet<string> iconAttempts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private bool iconsLoading;
        private bool disposed, paused, hasSnapshot;
        private long version;
        private string search = "";
        private int filter;
        private DateTime lastSuccess;
        private string currentWarning;
        private double startupMilliseconds, firstSnapshotMilliseconds;
        private int samplesApplied;
        private readonly bool smoke;
        private readonly int benchmarkSeconds;
        private string measurementPhase = "foreground";
        private readonly bool elevated;
        private string notice;
        private DateTime noticeUntil;
        public MainController(Window target, Stopwatch clock, string reportPath, bool smokeTest, int measureSeconds)
        {
            window = target; startup = clock; metricsPath = reportPath; smoke = smokeTest; benchmarkSeconds = measureSeconds;
            elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Element<ListBox>("AppList").ItemsSource = visibleApps;
            Element<TextBox>("SearchBox").TextChanged += SearchChanged;
            Element<TextBox>("SearchBox").GotKeyboardFocus += delegate { Element<Border>("SearchBorder").BorderBrush = Brush("#25664F"); };
            Element<TextBox>("SearchBox").LostKeyboardFocus += delegate { Element<Border>("SearchBorder").BorderBrush = Brush("#D5E1D8"); };
            Element<ComboBox>("FilterBox").SelectionChanged += delegate { filter = Element<ComboBox>("FilterBox").SelectedIndex; RefreshFilter(); };
            window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ButtonClicked), true);
            window.PreviewKeyDown += KeyDown;
            window.ContentRendered += delegate { if (startupMilliseconds <= 0) { startupMilliseconds = startup.Elapsed.TotalMilliseconds; WriteMetrics(); } };
            window.StateChanged += delegate { if (window.WindowState != WindowState.Minimized) RenderLatest(); };
            window.IsVisibleChanged += delegate { if (window.IsVisible) RenderLatest(); };
            window.Closed += delegate { Dispose(); };
            searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
            searchTimer.Tick += delegate { searchTimer.Stop(); search = Element<TextBox>("SearchBox").Text.Trim(); RefreshFilter(); };
            trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            trayPause = new Forms.ToolStripMenuItem("暂停界面刷新", null, delegate { OnUI(TogglePause); });
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开 NetPeek", null, delegate { OnUI(ShowWindow); }); menu.Items.Add(trayPause);
            menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出 NetPeek", null, delegate { OnUI(window.Close); });
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "NetPeek · 本地网络连接", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { OnUI(ShowWindow); };
            if (trayIcon != null) {
                var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                image.Freeze(); window.Icon = image;
            }
            collection = new CollectionLoop();
            renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            renderTimer.Tick += delegate {
                if (smoke && startup.Elapsed.TotalSeconds >= 5) { WriteMetrics(); window.Close(); return; }
                if (benchmarkSeconds > 0 && startupMilliseconds > 0) {
                    double elapsed = startup.Elapsed.TotalSeconds - startupMilliseconds / 1000;
                    if (elapsed >= 2 * benchmarkSeconds + 12) { WriteMetrics(); window.Close(); return; }
                    if (measurementPhase == "foreground" && elapsed >= benchmarkSeconds + 5) { measurementPhase = "background"; window.WindowState = WindowState.Minimized; WriteMetrics(); }
                }
                if (window.IsVisible && window.WindowState != WindowState.Minimized) RenderLatest();
            };
            renderTimer.Start();
            SystemEvents.PowerModeChanged += PowerChanged;
        }
        private T Element<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        private static Brush Brush(string color) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); b.Freeze(); return b; }
        private void OnUI(Action action) { if (!disposed && !window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(action); }
        public void ShowWindow() { if (disposed) return; window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); RenderLatest(); }
        private void SearchChanged(object sender, TextChangedEventArgs e)
        {
            Element<TextBlock>("SearchPlaceholder").Visibility = Element<TextBox>("SearchBox").Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            searchTimer.Stop(); searchTimer.Start();
        }
        private bool FilterApp(object item)
        {
            var app = (ApplicationView)item;
            if (!app.Matches(search)) return false;
            return filter == 0 || filter == 1 && app.Established > 0 || filter == 2 && app.Established == 0 && app.CurrentCount > 0 || filter == 3 && app.CurrentCount == 0;
        }
        private void RefreshFilter()
        {
            // Reconcile membership without a collection Reset: retained rows keep their controls,
            // keyboard focus, expansion and DataGrid selection when other apps appear/disappear.
            var desired = store.Applications.Where(a => FilterApp(a)).ToList();
            var keys = new HashSet<string>(desired.Select(a => a.Key), StringComparer.OrdinalIgnoreCase);
            for (int i = visibleApps.Count - 1; i >= 0; i--) if (!keys.Contains(visibleApps[i].Key)) visibleApps.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++) {
                if (i < visibleApps.Count && Object.ReferenceEquals(visibleApps[i], desired[i])) continue;
                int previous = visibleApps.IndexOf(desired[i]);
                if (previous >= 0) visibleApps.Move(previous, i); else visibleApps.Insert(i, desired[i]);
            }
            UpdateResult();
        }
        private void UpdateResult()
        {
            int count = visibleApps.Count;
            Element<TextBlock>("ResultText").Text = count + " 个应用" + (search.Length > 0 || filter != 0 ? " · 已筛选" : " · 列表顺序保持稳定");
            bool empty = hasSnapshot && count == 0;
            Element<StackPanel>("EmptyPanel").Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            Element<TextBlock>("EmptyTitle").Text = search.Length > 0 || filter != 0 ? "没有找到匹配的应用" : "这里暂时很安静";
            Element<TextBlock>("EmptyDescription").Text = search.Length > 0 || filter != 0 ? "换个关键词或选择“全部应用”试试。" : "发现网络端点后，应用会自动出现在这里。";
        }
        private void RenderLatest()
        {
            if (disposed || collection == null || paused) { UpdateStatus(); return; }
            NetworkSnapshot snapshot; long next; List<Observation> observations; bool truncated;
            if (collection.Read(version, out next, out snapshot, out observations, out truncated)) {
                version = next;
                if (!snapshot.IsComplete) {
                    currentWarning = "采集不完整。" + String.Join("；", snapshot.Warnings ?? new List<string>()) + (hasSnapshot ? " 正在保留上次完整画面，自动重试中。" : " 自动重试中；暂不能确认当前连接。");
                    if (!hasSnapshot) { Element<TextBlock>("EmptyTitle").Text = "暂时无法读取完整连接"; Element<TextBlock>("EmptyDescription").Text = "请稍后重试。受保护进程的路径可能不可读。"; Element<StackPanel>("EmptyPanel").Visibility = Visibility.Visible; }
                } else {
                    currentWarning = snapshot.Warnings != null && snapshot.Warnings.Count > 0 ? String.Join("；", snapshot.Warnings) : null;
                    // New apps are sorted only at their first appearance. Existing positions never move.
                    snapshot.Endpoints = snapshot.Endpoints.OrderBy(e => e.Process.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Process.AppKey, StringComparer.OrdinalIgnoreCase).ToList();
                    store.Apply(snapshot, observations); hasSnapshot = true; lastSuccess = snapshot.CapturedAtUtc; samplesApplied++;
                    if (firstSnapshotMilliseconds <= 0) { firstSnapshotMilliseconds = startup.Elapsed.TotalMilliseconds; WriteMetrics(); }
                    Element<TextBlock>("TcpCount").Text = snapshot.Endpoints.Count(e => e.Protocol == "TCP" && e.State == "ESTABLISHED").ToString();
                    Element<TextBlock>("ListenCount").Text = snapshot.Endpoints.Count(e => e.Protocol == "TCP" && e.State == "LISTEN").ToString();
                    Element<TextBlock>("UdpCount").Text = snapshot.Endpoints.Count(e => e.Protocol == "UDP").ToString();
                    RefreshFilter(); LoadIcons();
                    if (truncated || store.HistoryTruncated) currentWarning = (currentWarning ?? "") + " 历史记录已达 12,000 条上限，最早记录已滚动移出。";
                }
            }
            UpdateStatus();
        }
        private void UpdateStatus()
        {
            if (disposed) return;
            bool stale = hasSnapshot && (DateTime.UtcNow - lastSuccess).TotalSeconds > 6;
            string status = paused ? "界面已暂停" : !String.IsNullOrEmpty(currentWarning) || stale ? "采集需留意" : hasSnapshot ? "正在采集" : "正在读取连接";
            Element<TextBlock>("StatusText").Text = status;
            Element<System.Windows.Shapes.Ellipse>("StatusDot").Fill = Brush(paused || stale || !String.IsNullOrEmpty(currentWarning) ? "#9A712D" : "#397A58");
            string warning = currentWarning;
            if (paused) warning = "画面暂停于 " + (hasSnapshot ? lastSuccess.ToLocalTime().ToString("HH:mm:ss") : "首次采样前") + "。后台继续尝试采集；继续后显示最新状态。";
            else if (stale && String.IsNullOrEmpty(warning)) warning = "当前画面已超过 6 秒未更新，暂不能代表实时连接。正在重新读取…";
            Element<Border>("WarningPanel").Visibility = String.IsNullOrEmpty(warning) ? Visibility.Collapsed : Visibility.Visible;
            Element<TextBlock>("WarningText").Text = warning ?? "";
            Element<Button>("RetryButton").Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
            Element<TextBlock>("LastUpdated").Text = hasSnapshot ? (paused ? "画面时间 " : "更新于 ") + lastSuccess.ToLocalTime().ToString("HH:mm:ss") : "等待首次采样";
            Element<TextBlock>("Footnote").Text = notice != null && DateTime.UtcNow < noticeUntil ? notice : "IPv4 + IPv6 · TCP + UDP · " + (elevated ? "当前以管理员权限运行" : "普通用户权限");
        }
        private async void LoadIcons()
        {
            if (iconsLoading || disposed) return;
            var pending = store.Applications.Where(a => !String.IsNullOrEmpty(a.Path) && !iconAttempts.Contains(a.Path)).Take(16).ToList();
            if (pending.Count == 0) return;
            iconsLoading = true;
            try {
                foreach (var app in pending) {
                    if (disposed) break;
                    iconAttempts.Add(app.Path);
                    ImageSource image;
                    if (!iconCache.TryGetValue(app.Path, out image)) { string path = app.Path; image = await Task.Run(() => ExecutableIcons.Load(path)); if (image != null) iconCache[path] = image; }
                    if (!disposed) app.Icon = image;
                }
                // The view retains its own frozen icon. Bound caches so transient programs cannot grow them indefinitely.
                if (iconCache.Count > 256) iconCache.Clear();
                if (iconAttempts.Count > 512) { iconAttempts.Clear(); foreach (var a in store.Applications.Where(a => a.Icon != null).Take(512)) iconAttempts.Add(a.Path); }
            } finally { iconsLoading = false; }
        }
        private void TogglePause()
        {
            paused = !paused;
            Element<Button>("PauseButton").Content = paused ? "继续刷新" : "暂停刷新";
            trayPause.Text = paused ? "继续界面刷新" : "暂停界面刷新";
            if (!paused) { collection.Request(); RenderLatest(); } else UpdateStatus();
        }
        private void ButtonClicked(object sender, RoutedEventArgs e)
        {
            // ListBox/DataGrid can retarget routed Source to their item container.
            var button = e.OriginalSource as Button ?? e.Source as Button; if (button == null) return;
            switch (button.Name) {
                case "PauseButton": TogglePause(); break;
                case "RetryButton": collection.Request(); Notice("已请求重新采集"); break;
                case "InfoButton": Element<Border>("InfoPanel").Visibility = Element<Border>("InfoPanel").Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; break;
                case "CloseInfoButton": Element<Border>("InfoPanel").Visibility = Visibility.Collapsed; break;
                case "TrayButton": window.Hide(); break;
                case "ExitButton": window.Close(); break;
            }
            if ((string)button.Tag == "CopyPath") { var app = button.DataContext as ApplicationView; if (app != null && !String.IsNullOrEmpty(app.Path)) Copy(app.Path); else Notice("此应用的路径未知，无法复制"); }
            if ((string)button.Tag == "CopyEndpoint") { var endpoint = button.DataContext as EndpointView; if (endpoint != null) Copy(endpoint.CopyText); }
        }
        private async void Copy(string text)
        {
            for (int attempt = 0; attempt < 4 && !disposed; attempt++) {
                try { Clipboard.SetText(text); Notice("已复制：" + text); return; }
                catch (System.Runtime.InteropServices.COMException) {
                    // Some clipboard managers accept the data but temporarily block OLE flush.
                    try { if (Clipboard.GetText() == text) { Notice("已复制：" + text); return; } } catch (System.Runtime.InteropServices.COMException) { }
                }
                await Task.Delay(25 * (attempt + 1));
            }
            if (!disposed) Notice("剪贴板正被其他应用使用，请重试");
        }
        private void Notice(string text)
        {
            notice = text; noticeUntil = DateTime.UtcNow.AddSeconds(3); UpdateStatus();
            if (SystemParameters.ClientAreaAnimation) Element<TextBlock>("Footnote").BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        }
        private void KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control) {
                if (e.Key == Key.F) { Element<TextBox>("SearchBox").Focus(); Element<TextBox>("SearchBox").SelectAll(); e.Handled = true; }
                else if (e.Key == Key.P) { TogglePause(); e.Handled = true; }
                else if (e.Key == Key.C && !(Keyboard.FocusedElement is TextBox)) {
                    DependencyObject item = Keyboard.FocusedElement as DependencyObject;
                    while (item != null && !(item is DataGrid)) item = VisualTreeHelper.GetParent(item);
                    if (item is DataGrid) { var endpoint = ((DataGrid)item).SelectedItem as EndpointView; if (endpoint != null) { Copy(endpoint.CopyText); e.Handled = true; } }
                }
            } else if (e.Key == Key.F5) { collection.Request(); Notice(paused ? "后台已请求刷新；画面仍处于暂停状态" : "已请求重新采集"); e.Handled = true; }
            else if (e.Key == Key.Escape) { Element<TextBox>("SearchBox").Clear(); Element<Border>("InfoPanel").Visibility = Visibility.Collapsed; }
        }
        private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) OnUI(delegate { collection.Request(); UpdateStatus(); }); }
        private void WriteMetrics()
        {
            if (metricsPath == null) return;
            JsonFile.Write(metricsPath, new { StartupMilliseconds = Math.Round(startupMilliseconds, 2), FirstSnapshotMilliseconds = Math.Round(firstSnapshotMilliseconds, 2), Phase = measurementPhase, ProcessId = Process.GetCurrentProcess().Id, IsElevated = elevated, SamplesApplied = samplesApplied, ApplicationCount = store.Applications.Count, EndpointCount = store.Applications.Sum(a => a.CurrentCount), Success = hasSnapshot, CapturedAtUtc = DateTime.UtcNow.ToString("o") });
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            SystemEvents.PowerModeChanged -= PowerChanged;
            if (renderTimer != null) renderTimer.Stop(); if (searchTimer != null) searchTimer.Stop();
            if (collection != null) collection.Dispose();
            if (tray != null) { tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
            if (trayIcon != null) trayIcon.Dispose();
        }
    }
}
