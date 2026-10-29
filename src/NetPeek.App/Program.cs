using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Markup;
using NetPeek.Core;

namespace NetPeek
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var startup = Stopwatch.StartNew();
            try {
                if (args.Length == 2 && args[0] == "--snapshot") {
                    using (var collector = new NetworkCollector()) {
                        var snapshot = collector.Capture();
                        JsonFile.Write(args[1], new { CapturedAtUtc = snapshot.CapturedAtUtc.ToString("o"), snapshot.IsComplete, snapshot.Warnings, snapshot.Endpoints });
                        return snapshot.IsComplete ? 0 : 2;
                    }
                }
                bool smoke = args.Length == 2 && args[0] == "--smoke-test";
                int benchmarkSeconds = 0;
                bool benchmark = args.Length >= 2 && args.Length <= 3 && args[0] == "--benchmark";
                if (benchmark && args.Length == 3 && (!Int32.TryParse(args[2], out benchmarkSeconds) || benchmarkSeconds < 5 || benchmarkSeconds > 120)) throw new ArgumentException("基准采样时长须为 5–120 秒。");
                string metricsPath = smoke || benchmark ? args[1] : null;
                if (args.Length > 0 && metricsPath == null) { MessageBox.Show("支持的参数：--snapshot <文件>、--benchmark <文件>、--smoke-test <文件>", "NetPeek"); return 1; }
                string sid = WindowsIdentity.GetCurrent().User.Value;
                bool created;
                using (var mutex = new Mutex(true, "Local\\NetPeek.Desktop." + sid, out created))
                using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\NetPeek.Activate." + sid))
                {
                    if (!created) { activation.Set(); return 0; }
                    try {
                        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                        app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) {
                            LogFailure(e.Exception); e.Handled = true;
                            MessageBox.Show("界面遇到了问题，NetPeek 将退出。可重新打开重试。\n诊断记录保存在 %LOCALAPPDATA%\\NetPeek\\last-error.txt。", "NetPeek", MessageBoxButton.OK, MessageBoxImage.Warning);
                            app.Shutdown(1);
                        };
                        Window window;
                        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NetPeek.MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
                        app.MainWindow = window;
                        using (var controller = new MainController(window, startup, metricsPath, smoke, benchmarkSeconds)) {
                            RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(activation, delegate { if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(new Action(controller.ShowWindow)); }, null, Timeout.Infinite, false);
                            try { app.Run(window); }
                            finally { registration.Unregister(null); }
                        }
                        return 0;
                    } finally { mutex.ReleaseMutex(); }
                }
            } catch (Exception ex) {
                LogFailure(ex);
                if (args.Length == 2) {
                    try { JsonFile.Write(args[1], new { Error = ex.Message, Success = false }); } catch { }
                } else MessageBox.Show("NetPeek 未能启动。\n" + ex.Message + "\n\n诊断记录：%LOCALAPPDATA%\\NetPeek\\last-error.txt", "NetPeek", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        private static void LogFailure(Exception error)
        {
            try {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPeek");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "last-error.txt"), DateTime.UtcNow.ToString("o") + "\r\n" + error, Encoding.UTF8);
            } catch { }
        }
    }

    internal static class JsonFile
    {
        public static void Write(string path, object data)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 64 };
            File.WriteAllText(full, serializer.Serialize(data), new UTF8Encoding(false));
        }
    }

    // A single serial worker owns the collector; only the latest snapshot is retained.
    internal sealed class CollectionLoop : IDisposable
    {
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly object gate = new object();
        private readonly Thread thread;
        private volatile bool stopping;
        private NetworkSnapshot latest;
        private readonly CaptureHistory history = new CaptureHistory();
        private long sequence;
        public CollectionLoop() { thread = new Thread(Run) { IsBackground = true, Name = "NetPeek connection sampler" }; thread.Start(); }
        public void Request() { if (!stopping) { try { wake.Set(); } catch (ObjectDisposedException) { } } }
        public bool Read(long previous, out long version, out NetworkSnapshot snapshot, out List<Observation> observations, out bool truncated)
        {
            lock (gate) {
                version = sequence; snapshot = latest; observations = null; truncated = history.Truncated;
                if (latest == null || previous == sequence) return false;
                observations = history.Snapshot(); return true;
            }
        }
        private void Run()
        {
            try {
                using (var collector = new NetworkCollector()) {
                    while (!stopping) {
                        NetworkSnapshot snapshot;
                        try { snapshot = collector.Capture(); }
                        catch (Exception ex) { snapshot = new NetworkSnapshot { CapturedAtUtc = DateTime.UtcNow, IsComplete = false, Endpoints = new List<EndpointRecord>(), Warnings = new List<string> { "无法读取系统连接表：" + ex.Message } }; }
                        lock (gate) { history.Accept(snapshot); latest = snapshot; sequence++; }
                        if (stopping) break;
                        wake.WaitOne(1500);
                    }
                }
            } finally { wake.Dispose(); }
        }
        public void Dispose()
        {
            if (stopping) return; stopping = true;
            try { wake.Set(); } catch (ObjectDisposedException) { } // Worker may already have left its final read.
            thread.Join(3000); // OS table reads are synchronous; never hold UI indefinitely on shutdown.
        }
    }
}
