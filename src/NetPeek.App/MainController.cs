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
        private readonly RunEvidence evidence;
        private readonly ConnectionStore store = new ConnectionStore();
        private readonly CollectionLoop collection;
        private readonly DispatcherTimer renderTimer;
        private readonly DispatcherTimer searchTimer;
        private readonly ObservableCollection<ApplicationView> visibleApps = new ObservableCollection<ApplicationView>();
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ToolStripMenuItem trayPause;
        private readonly System.Drawing.Icon trayIcon;
        private readonly IconCache<ImageSource> iconCache = new IconCache<ImageSource>(256, TimeSpan.FromSeconds(5), () => DateTime.UtcNow,
            path => Task.Run(() => ExecutableIcons.Load(path)));
        private static readonly Brush HealthyBrush = Brush("#397A58"), AttentionBrush = Brush("#9A712D"), SearchFocusBrush = Brush("#25664F"), SearchIdleBrush = Brush("#D5E1D8");
        private static readonly Geometry SearchGlyph = Glyph("M 19,19 L 27,27 M 22,12 A 10,10 0 1 1 2,12 A 10,10 0 1 1 22,12");
        private static readonly Geometry EmptyGlyph = Glyph("M 14,15 A 6,6 0 1 1 2,15 A 6,6 0 1 1 14,15 M 30,15 A 6,6 0 1 1 18,15 A 6,6 0 1 1 30,15");
        private static readonly Geometry FailureGlyph = Glyph("M 16,3 L 16,18 M 16,24 L 16,26");
        private bool faultScheduled;
        private bool iconsLoading;
        private bool disposed, paused, hasSnapshot;
        private long version;
        private string search = "";
        private string searchError;
        private int filter, timeFilter, matchedEndpoints;
        private bool changingFilters;
        private int queryGeneration;
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
        private ContextMenu endpointMenu;
        private Window infoWindow, warningWindow;
        public MainController(Window target, Stopwatch clock, RunEvidence runEvidence, bool smokeTest, int measureSeconds)
        {
            window = target; startup = clock; evidence = runEvidence; smoke = smokeTest; benchmarkSeconds = measureSeconds;
            WindowLayout.Attach(window);
            elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Element<ListBox>("AppList").ItemsSource = visibleApps;
            Element<TextBox>("SearchBox").TextChanged += SearchChanged;
            Element<TextBox>("SearchBox").GotKeyboardFocus += delegate { Element<Border>("SearchBorder").BorderBrush = SearchFocusBrush; };
            Element<TextBox>("SearchBox").LostKeyboardFocus += delegate { Element<Border>("SearchBorder").BorderBrush = SearchIdleBrush; };
            Element<ComboBox>("FilterBox").SelectionChanged += delegate { filter = Element<ComboBox>("FilterBox").SelectedIndex; if (!changingFilters) RefreshFilter(true); };
            Element<ComboBox>("TimeFilterBox").SelectionChanged += delegate { timeFilter = Element<ComboBox>("TimeFilterBox").SelectedIndex; if (!changingFilters) RefreshFilter(true); };
            window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ButtonClicked), true);
            window.PreviewKeyDown += KeyDown;
            window.PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) {
                FrameworkElement menuTarget; EndpointView endpoint = FindEndpoint(e.OriginalSource as DependencyObject, out menuTarget);
                if (endpoint != null) { ShowCopyMenu(menuTarget, endpoint); e.Handled = true; }
            };
            window.ContentRendered += delegate { if (startupMilliseconds <= 0) { startupMilliseconds = startup.Elapsed.TotalMilliseconds; WriteMetrics(); } };
            window.StateChanged += delegate { if (window.WindowState != WindowState.Minimized) RenderLatest(); };
            window.IsVisibleChanged += delegate { if (window.IsVisible) RenderLatest(); };
            window.Closed += delegate { Dispose(); };
            searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
            searchTimer.Tick += delegate { searchTimer.Stop(); search = Element<TextBox>("SearchBox").Text.Trim(); RefreshFilter(true); };
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
            collection = new CollectionLoop(smoke && evidence != null ? evidence.TestFault : null);
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
            UpdateResult();
        }
        private T Element<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        private static Brush Brush(string color) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); b.Freeze(); return b; }
        private static Geometry Glyph(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
        private void SetText(string name, string value) { var element = Element<TextBlock>(name); if (element.Text != value) element.Text = value; }
        private void OnUI(Action action) { if (!disposed && !window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(action); }
        public void ShowWindow() { if (disposed) return; window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); RenderLatest(); }
        private void SearchChanged(object sender, TextChangedEventArgs e)
        {
            Element<TextBlock>("SearchPlaceholder").Visibility = Element<TextBox>("SearchBox").Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            searchTimer.Stop(); searchTimer.Start();
        }
        private void RefreshFilter()
        {
            RefreshFilter(false);
        }
        private void RefreshFilter(bool locate)
        {
            var query = new EndpointFilter(search, filter, timeFilter);
            searchError = query.Error;
            var desired = new List<ApplicationView>(); matchedEndpoints = 0;
            foreach (var app in store.Applications) {
                int count = query.Apply(app);
                if (count > 0) { desired.Add(app); matchedEndpoints += count; }
            }
            DisplayCollection.Reconcile(visibleApps, desired);
            UpdateResult();
            // Locate once for a user query, never again on a background sample.
            if (locate && query.IsActive && visibleApps.Count > 0) {
                int generation = ++queryGeneration;
                var first = visibleApps[0]; first.IsExpanded = true;
                first.DisplayProcesses[0].IsExpanded = true;
                Element<ListBox>("AppList").ScrollIntoView(first);
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                    if (disposed || generation != queryGeneration || !visibleApps.Contains(first) || !first.IsExpanded || first.DisplayProcesses.Count == 0) return;
                    var process = first.DisplayProcesses[0]; if (!process.IsExpanded) return;
                    var container = Element<ListBox>("AppList").ItemContainerGenerator.ContainerFromItem(first);
                    var grid = FindGrid(container, process);
                    if (grid != null && grid.Items.Count > 0) {
                        grid.ScrollIntoView(grid.Items[0]); grid.UpdateLayout();
                        grid.BringIntoView(new Rect(0, 0, Math.Min(335, grid.ActualWidth), Math.Min(70, grid.ActualHeight)));
                    }
                }));
            } else if (locate) { queryGeneration++; }
        }
        private static DataGrid FindGrid(DependencyObject parent, ProcessView process)
        {
            if (parent == null) return null;
            var grid = parent as DataGrid;
            if (grid != null && Object.ReferenceEquals(grid.DataContext, process)) return grid;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
                var found = FindGrid(VisualTreeHelper.GetChild(parent, i), process); if (found != null) return found;
            }
            return null;
        }
        private void SetFilters(string text)
        {
            changingFilters = true;
            try {
                Element<TextBox>("SearchBox").Text = text; searchTimer.Stop(); search = text.Trim();
                filter = timeFilter = 0;
                Element<ComboBox>("FilterBox").SelectedIndex = 0; Element<ComboBox>("TimeFilterBox").SelectedIndex = 0;
            } finally { changingFilters = false; }
            RefreshFilter(true);
        }
        private void UpdateResult()
        {
            int count = visibleApps.Count;
            bool filtered = search.Length > 0 || filter != 0 || timeFilter != 0;
            SetText("ResultText", hasSnapshot ? matchedEndpoints + " 个" + (filtered ? "匹配" : "") + "端点 · " + count + " 个应用" : "正在发现应用…");
            bool showEmpty = !hasSnapshot || count == 0;
            Element<StackPanel>("EmptyPanel").Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
            string title, description; Geometry glyph;
            if (!hasSnapshot && currentWarning != null) {
                title = "暂时无法读取完整连接"; description = "请稍后重试。受保护进程的路径可能不可读。"; glyph = FailureGlyph;
            } else if (!hasSnapshot) {
                title = "正在读取本机连接…"; description = "正在获取 Windows 提供的应用与端点信息。"; glyph = EmptyGlyph;
            } else if (searchError != null) {
                title = "查询格式不正确"; description = searchError; glyph = SearchGlyph;
            } else if (filtered) {
                title = "没有找到匹配的端点"; description = "换个关键词或时间范围，也可以清空筛选。"; glyph = SearchGlyph;
            } else {
                title = "这里暂时很安静"; description = "发现网络端点后，应用会自动出现在这里。"; glyph = EmptyGlyph;
            }
            SetText("EmptyTitle", title); SetText("EmptyDescription", description);
            Element<System.Windows.Shapes.Path>("EmptyGlyph").Data = glyph;
            Element<Button>("ResetFiltersButton").Visibility = hasSnapshot && count == 0 && filtered ? Visibility.Visible : Visibility.Collapsed;
            Element<Button>("ClearSearchButton").Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        }
        private void RenderLatest()
        {
            if (disposed || collection == null || paused) { UpdateStatus(); return; }
            NetworkSnapshot snapshot; long next; List<Observation> observations; bool truncated;
            if (collection.Read(version, out next, out snapshot, out observations, out truncated)) {
                version = next;
                if (!snapshot.IsComplete) {
                    currentWarning = "采集不完整。" + String.Join("；", snapshot.Warnings ?? new List<string>()) + (hasSnapshot ? " 正在保留上次完整画面，自动重试中。" : " 自动重试中；暂不能确认当前连接。");
                    if (!hasSnapshot) UpdateResult();
                } else {
                    currentWarning = snapshot.Warnings != null && snapshot.Warnings.Count > 0 ? String.Join("；", snapshot.Warnings) : null;
                    // New apps are sorted only at their first appearance. Existing positions never move.
                    snapshot.Endpoints = snapshot.Endpoints.OrderBy(e => e.Process.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Process.AppKey, StringComparer.OrdinalIgnoreCase).ToList();
                    bool changed = store.Apply(snapshot, observations); bool first = !hasSnapshot; hasSnapshot = true; lastSuccess = snapshot.CapturedAtUtc; samplesApplied++;
                    if (firstSnapshotMilliseconds <= 0) { firstSnapshotMilliseconds = startup.Elapsed.TotalMilliseconds; WriteMetrics(); }
                    SetText("TcpCount", snapshot.Endpoints.Count(e => e.Protocol == "TCP" && e.State == "ESTABLISHED").ToString());
                    SetText("ListenCount", snapshot.Endpoints.Count(e => e.Protocol == "TCP" && e.State == "LISTEN").ToString());
                    SetText("UdpCount", snapshot.Endpoints.Count(e => e.Protocol == "UDP").ToString());
                    if (changed || first) RefreshFilter();
                    LoadIcons();
                }
                if (truncated || store.HistoryTruncated) currentWarning = (currentWarning ?? "") + (snapshot.IsComplete
                    ? " 历史记录已达 12,000 条上限，最早记录已滚动移出。"
                    : " 上次完整采样的历史曾达到 12,000 条上限，部分最早记录已移出。");
            }
            UpdateStatus();
        }
        private void UpdateStatus()
        {
            UpdateStatusAt(DateTime.UtcNow);
        }
        private void UpdateStatusAt(DateTime now)
        {
            if (disposed) return;
            bool stale = hasSnapshot && (now - lastSuccess).TotalSeconds > 6;
            string status = paused ? "界面已暂停" : !String.IsNullOrEmpty(currentWarning) || stale ? "采集需留意" : hasSnapshot ? "正在采集" : "正在读取连接";
            SetText("StatusText", status);
            Element<System.Windows.Shapes.Ellipse>("StatusDot").Fill = paused || stale || !String.IsNullOrEmpty(currentWarning) ? AttentionBrush : HealthyBrush;
            string warning = currentWarning;
            if (paused) warning = "画面暂停于 " + (hasSnapshot ? lastSuccess.ToLocalTime().ToString("HH:mm:ss") : "首次采样前") + "。后台继续尝试采集；继续后显示最新状态。";
            else if (stale) warning = "当前画面已超过 6 秒未更新，暂不能代表实时连接。" +
                (String.IsNullOrEmpty(warning) ? "正在重新读取…" : " " + warning);
            Element<Border>("WarningPanel").Visibility = String.IsNullOrEmpty(warning) ? Visibility.Collapsed : Visibility.Visible;
            SetText("WarningText", warning ?? "");
            Element<Button>("RetryButton").Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
            SetText("LastUpdated", hasSnapshot ? (paused ? "画面时间 " : "更新于 ") + lastSuccess.ToLocalTime().ToString("HH:mm:ss") : "等待首次采样");
            SetText("Footnote", notice != null && now < noticeUntil ? notice : "IPv4 + IPv6 · TCP + UDP · " + (elevated ? "当前以管理员权限运行" : "普通用户权限"));
        }
        private async void LoadIcons()
        {
            if (iconsLoading || disposed) return;
            var pending = store.Applications.Where(a => a.Icon == null && !String.IsNullOrEmpty(a.Path) && iconCache.CanRequest(a.Path)).Take(16).ToList();
            if (pending.Count == 0) return;
            iconsLoading = true;
            try {
                foreach (var app in pending) {
                    if (disposed) break;
                    string path = app.Path;
                    ImageSource image = await iconCache.GetAsync(path);
                    // A load may finish after a row expires, is regrouped or the window exits.
                    if (!disposed && image != null && app.Path == path && store.Applications.Contains(app)) app.Icon = image;
                }
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
                case "ClearSearchButton": case "ResetFiltersButton":
                    SetFilters(""); Element<TextBox>("SearchBox").Focus(); break;
                case "PauseButton": TogglePause(); break;
                case "RetryButton": collection.Request(); Notice("已请求重新采集"); break;
                case "InfoButton": case "CompactInfoButton":
                    if (infoWindow != null) infoWindow.Close();
                    else { infoWindow = DetailWindows.Create(window, "数据说明", DetailWindows.HelpText); infoWindow.Owner = window; infoWindow.Closed += delegate { infoWindow = null; }; infoWindow.Show(); }
                    break;
                case "WarningDetailsButton":
                    if (warningWindow != null) warningWindow.Close();
                    warningWindow = DetailWindows.Create(window, "采集信息", "打开时的提示（" + DateTime.Now.ToString("HH:mm:ss") + "）：\n\n" + Element<TextBlock>("WarningText").Text);
                    warningWindow.Owner = window; warningWindow.Closed += delegate { warningWindow = null; }; warningWindow.Show(); break;
                case "TrayButton": case "CompactTrayButton": window.Hide(); break;
                case "ExitButton": case "CompactExitButton": window.Close(); break;
            }
            if ((string)button.Tag == "CopyPath") { var app = button.DataContext as ApplicationView; if (app != null && !String.IsNullOrEmpty(app.Path)) Copy(app.Path); else Notice("此应用的路径未知，无法复制"); }
            if ((string)button.Tag == "CopyEndpoint") { var endpoint = button.DataContext as EndpointView; if (endpoint != null) Copy(endpoint.CopyText); }
            if ((string)button.Tag == "CopyMenu") { var endpoint = button.DataContext as EndpointView; if (endpoint != null) ShowCopyMenu(button, endpoint); }
            if ((string)button.Tag == "ShowAppEndpoints") {
                var app = button.DataContext as ApplicationView;
                if (app != null) SetFilters(app.Path ?? "pid:" + app.Processes[0].Identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        private static EndpointView FindEndpoint(DependencyObject item, out FrameworkElement target)
        {
            target = null;
            while (item != null) {
                var element = item as FrameworkElement;
                if (element != null) {
                    var endpoint = element.DataContext as EndpointView;
                    if (endpoint == null && element is DataGrid) endpoint = ((DataGrid)element).SelectedItem as EndpointView;
                    if (endpoint != null) { target = element; return endpoint; }
                }
                item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
            }
            return null;
        }
        private ContextMenu CreateCopyMenu(EndpointView endpoint)
        {
            // Freeze the values when the menu opens, including the observed time.
            // A refresh while choosing an action cannot silently change what is copied.
            var menu = new ContextMenu { FontFamily = window.FontFamily, FontSize = 12, Foreground = window.Foreground };
            string local = endpoint.Local, remote = endpoint.CanCopyRemote ? endpoint.Remote : null, details = endpoint.DetailsText;
            var copyLocal = new MenuItem { Header = "复制本地端点" };
            copyLocal.Click += delegate { Copy(local); }; menu.Items.Add(copyLocal);
            var copyRemote = new MenuItem { Header = remote != null ? "复制远端端点" : endpoint.Record.Protocol == "UDP" ? "远端未知 · UDP 系统表不提供" : "远端不可用 · 此端点没有远端", IsEnabled = remote != null };
            copyRemote.Click += delegate { if (remote != null) Copy(remote); }; menu.Items.Add(copyRemote);
            menu.Items.Add(new Separator());
            var copyDetails = new MenuItem { Header = "复制完整记录" };
            copyDetails.Click += delegate { Copy(details, "已复制完整记录（含观察时间）"); }; menu.Items.Add(copyDetails);
            return menu;
        }
        private void ShowCopyMenu(FrameworkElement target, EndpointView endpoint)
        {
            if (endpointMenu != null) endpointMenu.IsOpen = false;
            endpointMenu = CreateCopyMenu(endpoint);
            endpointMenu.PlacementTarget = target; endpointMenu.Placement = PlacementMode.Bottom;
            endpointMenu.IsOpen = true;
        }
        private async void Copy(string text, string confirmation = null)
        {
            for (int attempt = 0; attempt < 4 && !disposed; attempt++) {
                try { Clipboard.SetText(text); Notice(confirmation ?? "已复制：" + text); return; }
                catch (System.Runtime.InteropServices.COMException) {
                    // Some clipboard managers accept the data but temporarily block OLE flush.
                    try { if (Clipboard.GetText() == text) { Notice(confirmation ?? "已复制：" + text); return; } } catch (System.Runtime.InteropServices.COMException) { }
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
            if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) {
                FrameworkElement target; var endpoint = FindEndpoint(Keyboard.FocusedElement as DependencyObject, out target);
                if (endpoint != null) { ShowCopyMenu(target, endpoint); e.Handled = true; return; }
            }
            if (e.Key == Key.Enter && Keyboard.FocusedElement == Element<TextBox>("SearchBox")) {
                searchTimer.Stop(); search = Element<TextBox>("SearchBox").Text.Trim(); RefreshFilter(true); e.Handled = true; return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control) {
                if (e.Key == Key.F) { Element<TextBox>("SearchBox").Focus(); Element<TextBox>("SearchBox").SelectAll(); e.Handled = true; }
                else if (e.Key == Key.P) { TogglePause(); e.Handled = true; }
                else if (e.Key == Key.C && !(Keyboard.FocusedElement is TextBox)) {
                    FrameworkElement target; var endpoint = FindEndpoint(Keyboard.FocusedElement as DependencyObject, out target);
                    if (endpoint != null) { Copy(endpoint.CopyText); e.Handled = true; }
                }
            } else if (e.Key == Key.F5) { collection.Request(); Notice(paused ? "后台已请求刷新；画面仍处于暂停状态" : "已请求重新采集"); e.Handled = true; }
            else if (e.Key == Key.Escape) { Element<TextBox>("SearchBox").Clear(); if (infoWindow != null) infoWindow.Close(); }
        }
        private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) OnUI(delegate { collection.Request(); UpdateStatus(); }); }
        private void WriteMetrics()
        {
            if (evidence == null) return;
            evidence.StartupMilliseconds = Math.Round(startupMilliseconds, 2);
            evidence.FirstSnapshotMilliseconds = Math.Round(firstSnapshotMilliseconds, 2);
            evidence.Phase = measurementPhase; evidence.IsElevated = elevated;
            evidence.SamplesApplied = samplesApplied; evidence.HasValidSnapshot = hasSnapshot && samplesApplied > 0;
            evidence.LastSuccessfulSnapshotUtc = hasSnapshot ? lastSuccess.ToString("o") : null;
            evidence.ApplicationCount = store.Applications.Count; evidence.EndpointCount = store.Applications.Sum(a => a.CurrentCount);
            evidence.Write();
            if (smoke && evidence.TestFault == "ui-after-report" && evidence.HasValidSnapshot && !faultScheduled) {
                faultScheduled = true; evidence.MarkFaultAfterReport();
                window.Dispatcher.BeginInvoke(new Action(delegate { throw new InvalidOperationException("显式 smoke 测试注入：有效报告写入后的 UI 异常"); }));
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            SystemEvents.PowerModeChanged -= PowerChanged;
            if (renderTimer != null) renderTimer.Stop(); if (searchTimer != null) searchTimer.Stop();
            if (endpointMenu != null) endpointMenu.IsOpen = false;
            if (infoWindow != null) infoWindow.Close(); if (warningWindow != null) warningWindow.Close();
            if (collection != null) collection.Dispose();
            iconCache.Dispose();
            if (tray != null) { tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
            if (trayIcon != null) trayIcon.Dispose();
        }
    }
}
