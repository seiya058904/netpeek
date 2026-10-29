using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NetPeek
{
    // Native work areas are physical pixels. WPF constraints are DIPs at the
    // window's current DPI; never apply the primary monitor's size to another one.
    internal sealed class WindowLayout
    {
        private readonly Window window;
        private IntPtr handle;
        private HwndSource source;
        private bool updating, queued, closed, moving;

        static WindowLayout()
        {
            // Loaded is a direct routed event: registering only on Window would
            // miss grids created inside virtualized application templates.
            EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.LoadedEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e) {
                var grid = (DataGrid)sender;
                grid.SizeChanged -= EndpointSizeChanged;
                grid.SizeChanged += EndpointSizeChanged;
                FitEndpointColumns(grid);
            }));
        }
        private WindowLayout(Window window)
        {
            this.window = window;
            window.SourceInitialized += Initialized;
            window.SizeChanged += delegate { AdaptContent(); QueueBounds(); };
            window.Loaded += delegate { QueueBounds(); };
            window.LocationChanged += delegate { QueueBounds(); };
            window.Closed += delegate { closed = true; if (source != null) source.RemoveHook(Messages); };
        }
        public static void Attach(Window window) { new WindowLayout(window); }
        private static void EndpointSizeChanged(object sender, SizeChangedEventArgs args) { FitEndpointColumns((DataGrid)sender); }
        private static void FitEndpointColumns(DataGrid grid)
        {
            // A frozen area cannot itself scroll. On exceptionally small work
            // areas, release it so every column (including Copy) stays reachable.
            grid.FrozenColumnCount = grid.ActualWidth >= 330 ? 3 : 0;
        }

        private void Initialized(object sender, EventArgs args)
        {
            handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle);
            if (source != null) source.AddHook(Messages);
            ApplyBounds();
        }
        private IntPtr Messages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Let WPF process the suggested DPI rectangle, then constrain it.
            if (message == 0x0231) moving = true; // Do not snap a user drag back before it can cross monitors.
            if (message == 0x0232) { moving = false; QueueBounds(); }
            if (message == 0x02E0 || message == 0x007E || message == 0x001A) QueueBounds();
            return IntPtr.Zero;
        }
        private void QueueBounds()
        {
            if (closed || moving || updating || queued || handle == IntPtr.Zero || window.Dispatcher.HasShutdownStarted) return;
            queued = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                queued = false; if (!closed) ApplyBounds();
            }));
        }
        internal static Size LogicalWorkArea(int pixelWidth, int pixelHeight, double scaleX, double scaleY)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0 || scaleX <= 0 || scaleY <= 0) throw new ArgumentOutOfRangeException("workArea");
            return new Size(pixelWidth / scaleX, pixelHeight / scaleY);
        }
        private void ApplyBounds()
        {
            if (updating || source == null || source.CompositionTarget == null) return;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return;
            var transform = source.CompositionTarget.TransformToDevice;
            Size available = LogicalWorkArea(monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top, transform.M11, transform.M22);
            updating = true;
            try {
                // Lower minima before clamping: even 200% on a small work area must fit.
                window.MinWidth = Math.Min(720, available.Width);
                window.MinHeight = Math.Min(480, available.Height);
                if (window.WindowState == WindowState.Normal) {
                    if (window.Width > available.Width) window.Width = available.Width;
                    if (window.Height > available.Height) window.Height = available.Height;
                    RectNative rect;
                    if (GetWindowRect(handle, out rect)) {
                        int width = Math.Min(rect.Right - rect.Left, monitor.Work.Right - monitor.Work.Left);
                        int height = Math.Min(rect.Bottom - rect.Top, monitor.Work.Bottom - monitor.Work.Top);
                        int left = Math.Max(monitor.Work.Left, Math.Min(rect.Left, monitor.Work.Right - width));
                        int top = Math.Max(monitor.Work.Top, Math.Min(rect.Top, monitor.Work.Bottom - height));
                        if (left != rect.Left || top != rect.Top || width != rect.Right - rect.Left || height != rect.Bottom - rect.Top)
                            SetWindowPos(handle, IntPtr.Zero, left, top, width, height, 0x0014); // NOZORDER | NOACTIVATE
                    }
                }
                AdaptContent();
            } finally { updating = false; }
        }
        private void AdaptContent()
        {
            if (closed) return;
            double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            bool narrow = width < 960, shortWindow = height < 600, compact = width < 700;
            ((ColumnDefinition)window.FindName("RailColumn")).Width = new GridLength(compact ? 0 : narrow ? 132 : 164);
            ((Border)window.FindName("RailPanel")).Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ((StackPanel)window.FindName("HeaderLabels")).Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ((StackPanel)window.FindName("CompactActions")).Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            ((Grid)window.FindName("RailContent")).Margin = narrow ? new Thickness(12, 16, 12, 12) : new Thickness(16, 20, 16, 16);
            ((Grid)window.FindName("MainPane")).Margin = height < 480 ? new Thickness(12, 6, 12, 4) : narrow ? new Thickness(14, 14, 14, 10) : new Thickness(24, 20, 24, 14);
            ((TextBlock)window.FindName("RailCaption")).Visibility = narrow || shortWindow ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)window.FindName("BrandTagline")).Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)window.FindName("HeaderSubtitle")).Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
            var summary = (Border)window.FindName("SummaryPanel");
            summary.Visibility = height < 560 ? Visibility.Collapsed : Visibility.Visible;
            summary.Margin = shortWindow ? new Thickness(0, 8, 0, 8) : new Thickness(0, 16, 0, 14);
            summary.Padding = new Thickness(0, 0, 0, shortWindow ? 6 : 12);
            ((ScrollViewer)window.FindName("InfoScroll")).MaxHeight = height < 480 ? 34 : shortWindow ? 80 : 135;
            ((ScrollViewer)window.FindName("WarningScroll")).MaxHeight = height < 480 ? 26 : shortWindow ? 42 : 72;
        }
        [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectNative Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rectangle);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
