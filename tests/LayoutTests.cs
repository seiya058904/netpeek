using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class LayoutTests
{
    private static Assembly product;
    private static Type layout;
    private static int passed;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            if (args.Length != 1) throw new ArgumentException("Pass the built NetPeek.exe path.");
            product = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            layout = product.GetType("NetPeek.WindowLayout", true);
            Console.WriteLine("PRODUCT " + product.Location + " version " + product.GetName().Version);
            WorkAreas();
            List<string> failures = new List<string>();
            foreach (Size size in new[] { new Size(1180, 800), new Size(720, 480), new Size(640, 360), new Size(512, 360) }) {
                try { WindowSize(size); }
                catch (Exception ex) { failures.Add(ex.Message); Console.Error.WriteLine("FAILED CASE " + size + ": " + ex.Message); }
            }
            if (failures.Count > 0) throw new Exception(String.Join(Environment.NewLine, failures));
            Console.WriteLine("SUCCESS " + passed + " in-process WPF assertions; no product entry point, sampling, sockets or visible window launched");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }
    private static void WorkAreas()
    {
        MethodInfo convert = layout.GetMethod("LogicalWorkArea", BindingFlags.Static | BindingFlags.NonPublic);
        Check(convert != null, "production work-area conversion is present in the built assembly");
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 }) {
            Size actual = (Size)convert.Invoke(null, new object[] { 1920, 1080, scale, scale });
            Check(Math.Abs(actual.Width * scale - 1920) < 0.001 && Math.Abs(actual.Height * scale - 1080) < 0.001, "1920x1080 work area converts at simulated " + (scale * 100) + "% DPI");
        }
        Size small = (Size)convert.Invoke(null, new object[] { 1024, 720, 2.0, 2.0 });
        Check(small.Width == 512 && small.Height == 360, "small 200% work area yields 512x360 DIPs");
        bool rejected = false; try { convert.Invoke(null, new object[] { 1920, 1080, 0.0, 1.0 }); } catch (TargetInvocationException ex) { rejected = ex.InnerException is ArgumentOutOfRangeException; }
        Check(rejected, "invalid DPI cannot create unbounded window constraints");
    }
    private static void WindowSize(Size size)
    {
        Window window;
        using (Stream resource = product.GetManifestResourceStream("NetPeek.MainWindow.xaml")) {
            if (resource == null) throw new InvalidOperationException("Built executable lacks NetPeek.MainWindow.xaml.");
            window = (Window)XamlReader.Load(resource);
        }
        try {
            window.ShowActivated = false;
            window.MinWidth = 0; window.MinHeight = 0;
            window.Width = size.Width; window.Height = size.Height;
            layout.GetMethod("Attach", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { window });
            RaiseSizeChanged(window);
            ListBox list = (ListBox)window.FindName("AppList");
            list.ItemsSource = new[] { Fixture() };
            ((FrameworkElement)window.FindName("EmptyPanel")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("InfoPanel")).Visibility = Visibility.Visible;
            ((FrameworkElement)window.FindName("WarningPanel")).Visibility = Visibility.Visible;
            ((TextBlock)window.FindName("WarningText")).Text = String.Join(" ", Enumerable.Repeat("部分端点表读取失败，保留上次完整快照；连接归属需要核验。", 12));
            Arrange(window, size);
            DataGrid grid = Descendants<DataGrid>((DependencyObject)window.Content).FirstOrDefault();
            if (grid == null) throw new Exception(size + ": actual endpoint DataGrid was not realized");
            grid.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, grid));
            Arrange(window, size);
            string label = size.Width + "x" + size.Height;
            Console.WriteLine("MEASURE " + label + " appList=" + list.ActualWidth.ToString("F1") + "x" + list.ActualHeight.ToString("F1") + " dataGrid=" + grid.ActualWidth.ToString("F1") + " frozen=" + grid.FrozenColumnCount);
            Console.WriteLine("COLUMNS " + label + " " + String.Join(", ", grid.Columns.Select(c => c.Header + "=" + c.ActualWidth.ToString("F1"))));
            Render(window, label);
            Check(list.ActualHeight >= 70, label + " keeps at least 70 DIPs of application list with info and warning panels open");
            Check(list.ActualWidth > 250 && grid.ActualWidth > 250, label + " actual endpoint content has usable width");
            Check(grid.Columns.Count == 7 && (string)grid.Columns[0].Header == "远端 IP : 端口" && (string)grid.Columns[1].Header == "状态" && (string)grid.Columns[2].Header == "复制", label + " uses actual remote/status/copy leading columns");
            double frozenWidth = grid.Columns.Take(3).Sum(c => c.ActualWidth);
            Check(frozenWidth <= grid.ActualWidth - 2 && grid.FrozenColumnCount == 3, label + " remote/status/copy fit in the default viewport");
            Check(grid.Columns[1].ActualWidth >= 90 && grid.Columns[2].ActualWidth >= 62 && grid.Columns[3].ActualWidth >= 84 && grid.Columns[5].ActualWidth >= 78 && grid.Columns[6].ActualWidth >= 88,
                label + " star address columns cannot squeeze state/copy/protocol labels below their usable widths");
            Check(grid.Items.Count == 3 && Descendants<DataGridRow>(grid).Any(), label + " binds and realizes the delivered executable's VM rows");
            FrameworkElement main = (FrameworkElement)window.FindName("MainPane");
            Point corner = main.TranslatePoint(new Point(main.ActualWidth, main.ActualHeight), (UIElement)window.Content);
            FrameworkElement clientRoot = (FrameworkElement)window.Content;
            Check(corner.X <= clientRoot.ActualWidth + 1 && corner.Y <= clientRoot.ActualHeight + 1, label + " main content stays inside the client area after standard window chrome");
            if (size.Width < 700) {
                Check(((FrameworkElement)window.FindName("RailPanel")).Visibility == Visibility.Collapsed && ((FrameworkElement)window.FindName("CompactActions")).Visibility == Visibility.Visible, label + " retains compact actions without consuming a sidebar column");
            }
            // Exercise the actual grid's production Loaded/SizeChanged hooks at a
            // viewport smaller than its leading columns; all columns must scroll.
            grid.Width = 280;
            Arrange(window, size);
            grid.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, grid));
            Arrange(window, size);
            ScrollViewer scroll = Descendants<ScrollViewer>(grid).FirstOrDefault();
            Check(grid.ActualWidth < 330 && grid.FrozenColumnCount == 0 && scroll != null && scroll.ScrollableWidth > 0, label + " narrow grid releases frozen columns and has horizontal overflow navigation");
            scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth); Pump(); grid.UpdateLayout();
            Check(scroll.HorizontalOffset > 0, label + " narrow endpoint columns can actually scroll horizontally");
            grid.ScrollIntoView(grid.Items[0], grid.Columns[2]); Pump(); grid.UpdateLayout();
            DataGridCell copy = Descendants<DataGridCell>(grid).FirstOrDefault(c => c.Column == grid.Columns[2]);
            if (copy == null) throw new Exception(label + ": copy column did not realize after ScrollIntoView");
            Point copyLeft = copy.TranslatePoint(new Point(), grid), copyRight = copy.TranslatePoint(new Point(copy.ActualWidth, 0), grid);
            Check(copyLeft.X >= -1 && copyRight.X <= grid.ActualWidth + 1 && Descendants<Button>(copy).Any(), label + " copy action becomes fully reachable after narrow horizontal scrolling");
        } finally { window.Close(); Pump(); }
    }
    private static object Fixture()
    {
        Type identityType = product.GetType("NetPeek.Core.ProcessIdentity", true);
        Type recordType = product.GetType("NetPeek.Core.EndpointRecord", true);
        Type appType = product.GetType("NetPeek.ApplicationView", true);
        Type processType = product.GetType("NetPeek.ProcessView", true);
        Type endpointType = product.GetType("NetPeek.EndpointView", true);
        object identity = Activator.CreateInstance(identityType);
        Set(identity, "ProcessId", 1234); Set(identity, "StartTimeUtcTicks", DateTime.UtcNow.Ticks);
        Set(identity, "InstanceId", "layout:1234"); Set(identity, "AppKey", "path:C:\\layout\\fixture.exe");
        Set(identity, "Name", "布局验证示例应用"); Set(identity, "Path", @"C:\layout\fixture.exe");
        object app = Activator.CreateInstance(appType, new[] { identity });
        object process = Activator.CreateInstance(processType, new[] { identity });
        IList endpoints = (IList)processType.GetProperty("Endpoints").GetValue(process, null);
        for (int i = 0; i < 3; i++) {
            object record = Activator.CreateInstance(recordType);
            Set(record, "Key", "layout:" + i); Set(record, "Protocol", "TCP"); Set(record, "AddressFamily", "IPv6");
            Set(record, "LocalAddress", "::1"); Set(record, "LocalPort", 45000 + i);
            Set(record, "RemoteAddress", "2001:db8:1234:5678:1234:5678:1234:5678"); Set(record, "RemotePort", 443);
            Set(record, "State", "ESTABLISHED"); Set(record, "Process", identity);
            endpoints.Add(Activator.CreateInstance(endpointType, new[] { record, (object)DateTime.UtcNow }));
        }
        ((IList)appType.GetProperty("Processes").GetValue(app, null)).Add(process);
        appType.GetMethod("Recount").Invoke(app, null);
        Set(app, "IsExpanded", true); Set(process, "IsExpanded", true);
        return app;
    }
    private static void Set(object target, string property, object value) { target.GetType().GetProperty(property).SetValue(target, value, null); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            T match = child as T; if (match != null) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Arrange(Window window, Size size)
    {
        Thickness frame = SystemParameters.WindowNonClientFrameThickness;
        Size client = new Size(Math.Max(1, size.Width - frame.Left - frame.Right), Math.Max(1, size.Height - frame.Top - frame.Bottom));
        for (int i = 0; i < 4; i++) {
            window.ApplyTemplate(); window.Measure(size); window.Arrange(new Rect(new Point(), size)); window.UpdateLayout();
            FrameworkElement root = (FrameworkElement)window.Content; root.Measure(client); root.Arrange(new Rect(new Point(), client)); root.UpdateLayout(); Pump();
        }
    }
    private static void RaiseSizeChanged(Window window)
    {
        // Hidden WPF windows do not receive an OS resize/layout pass. Raise the
        // actual SizeChanged routed event so Attach's production handler runs;
        // all control templates and resulting layout are measured normally.
        ConstructorInfo constructor = typeof(SizeChangedEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        Type infoType = constructor.GetParameters()[1].ParameterType;
        object info = Activator.CreateInstance(infoType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { window, new Size(), true, true }, null);
        SizeChangedEventArgs args = (SizeChangedEventArgs)constructor.Invoke(new[] { (object)window, info });
        args.RoutedEvent = FrameworkElement.SizeChangedEvent; window.RaiseEvent(args);
    }
    private static void Render(Window window, string label)
    {
        FrameworkElement root = (FrameworkElement)window.Content;
        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        DrawingVisual backdrop = new DrawingVisual();
        using (DrawingContext drawing = backdrop.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(backdrop); bitmap.Render(root);
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout-" + label + ".png");
        PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream output = File.Create(path)) encoder.Save(output);
        Console.WriteLine("ARTIFACT " + path + " (offscreen XAML fixture render; not an OS/DPI screenshot)");
    }
    private static void Pump()
    {
        DispatcherFrame frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
    }
}
