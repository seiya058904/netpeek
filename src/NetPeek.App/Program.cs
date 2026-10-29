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
            RunEvidence evidence = null;
            bool smoke = args.Length >= 2 && args[0] == "--smoke-test";
            bool snapshotMode = args.Length == 2 && args[0] == "--snapshot";
            DateTime invocationStarted = DateTime.UtcNow;
            try {
                if (snapshotMode) {
                    DateTime started = DateTime.UtcNow;
                    using (var collector = new NetworkCollector()) {
                        var snapshot = collector.Capture();
                        JsonFile.Write(args[1], new { RunId = Guid.NewGuid().ToString("D"), ProcessId = Process.GetCurrentProcess().Id,
                            StartedAtUtc = started.ToString("o"), ReportWrittenAtUtc = DateTime.UtcNow.ToString("o"),
                            Success = snapshot.IsComplete, CapturedAtUtc = snapshot.CapturedAtUtc.ToString("o"), snapshot.IsComplete, snapshot.Warnings, snapshot.Endpoints });
                        return snapshot.IsComplete ? 0 : 2;
                    }
                }
                int benchmarkSeconds = 0;
                bool benchmark = args.Length >= 2 && args.Length <= 3 && args[0] == "--benchmark";
                string requestedRunId = Guid.NewGuid().ToString("D"), fault = null;
                if (smoke) {
                    for (int i = 2; i < args.Length; i += 2) {
                        if (i + 1 >= args.Length) throw new ArgumentException("测试选项必须有值。");
                        if (args[i] == "--run-id") { Guid id; if (!Guid.TryParse(args[i + 1], out id)) throw new ArgumentException("测试 run-id 必须是 GUID。"); requestedRunId = id.ToString("D"); }
                        else if (args[i] == "--test-fault" && (args[i + 1] == "ui-after-report" || args[i + 1] == "capture-always" || args[i + 1] == "empty-snapshot")) fault = args[i + 1];
                        else throw new ArgumentException("未知测试选项；故障注入仅允许显式 --smoke-test 使用。");
                    }
                }
                if (benchmark && args.Length == 3 && (!Int32.TryParse(args[2], out benchmarkSeconds) || benchmarkSeconds < 5 || benchmarkSeconds > 120)) throw new ArgumentException("基准采样时长须为 5–120 秒。");
                if (args.Length > 0 && !smoke && !benchmark) { MessageBox.Show("支持的参数：--snapshot <文件>、--benchmark <文件>、--smoke-test <文件>", "NetPeek"); return 1; }
                if (smoke || benchmark) evidence = new RunEvidence(args[1], requestedRunId, fault);
                string sid = WindowsIdentity.GetCurrent().User.Value;
                bool created;
                using (var mutex = new Mutex(true, "Local\\NetPeek.Desktop." + sid, out created))
                using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\NetPeek.Activate." + sid))
                {
                    if (!created) {
                        if (evidence != null) { evidence.Fail("另一个 NetPeek 实例正在运行，未执行本轮测试。"); evidence.Complete(3); return 3; }
                        activation.Set(); return 0;
                    }
                    try {
                        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                        app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) {
                            if (evidence == null) LogFailure(e.Exception);
                            e.Handled = true;
                            if (evidence != null) evidence.Fail(e.Exception.Message);
                            if (evidence == null) MessageBox.Show("界面遇到了问题，NetPeek 将退出。可重新打开重试。\n诊断记录保存在 %LOCALAPPDATA%\\NetPeek\\last-error.txt。", "NetPeek", MessageBoxButton.OK, MessageBoxImage.Warning);
                            app.Shutdown(1);
                        };
                        Window window;
                        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NetPeek.MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
                        app.MainWindow = window;
                        int exitCode;
                        using (var controller = new MainController(window, startup, evidence, smoke, benchmarkSeconds)) {
                            RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(activation, delegate { if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(new Action(controller.ShowWindow)); }, null, Timeout.Infinite, false);
                            try { exitCode = app.Run(window); }
                            finally { registration.Unregister(null); }
                        }
                        if (smoke && exitCode == 0 && (evidence == null || !evidence.HasValidSnapshot || evidence.SamplesApplied == 0)) exitCode = 2;
                        if (evidence != null) { if (exitCode == 2) evidence.Fail("本轮未应用任何有效完整快照。"); evidence.Complete(exitCode); }
                        return exitCode;
                    } finally { mutex.ReleaseMutex(); }
                }
            } catch (Exception ex) {
                if (!smoke && !snapshotMode && evidence == null) LogFailure(ex);
                if (snapshotMode) {
                    try { JsonFile.Write(args[1], new { RunId = Guid.NewGuid().ToString("D"), ProcessId = Process.GetCurrentProcess().Id, StartedAtUtc = invocationStarted.ToString("o"), ReportWrittenAtUtc = DateTime.UtcNow.ToString("o"), IsComplete = false, Success = false, Error = ex.Message }); } catch { }
                }
                else if (evidence != null) { try { evidence.Fail(ex.Message); evidence.Complete(1); } catch { } }
                else if (!smoke) MessageBox.Show("NetPeek 未能启动。\n" + ex.Message + "\n\n诊断记录：%LOCALAPPDATA%\\NetPeek\\last-error.txt", "NetPeek", MessageBoxButton.OK, MessageBoxImage.Error);
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

    // One report belongs to one process invocation. An earlier startup report is never final success.
    internal sealed class RunEvidence
    {
        private readonly string path;
        public string RunId { get; private set; }
        public int ProcessId { get; private set; }
        public string StartedAtUtc { get; private set; }
        public string TestFault { get; private set; }
        public string ReportWrittenAtUtc { get; private set; }
        public string CapturedAtUtc { get { return ReportWrittenAtUtc; } }
        public string LastSuccessfulSnapshotUtc { get; set; }
        public bool HasValidSnapshot { get; set; }
        public bool Completed { get; private set; }
        public int? ExitCode { get; private set; }
        public string Error { get; private set; }
        public bool Success { get { return Completed && ExitCode == 0 && HasValidSnapshot && SamplesApplied > 0 && Error == null; } }
        public bool ValidReportWrittenBeforeFault { get; private set; }
        public double StartupMilliseconds { get; set; }
        public double FirstSnapshotMilliseconds { get; set; }
        public string Phase { get; set; }
        public bool IsElevated { get; set; }
        public int SamplesApplied { get; set; }
        public int ApplicationCount { get; set; }
        public int EndpointCount { get; set; }
        public RunEvidence(string destination, string runId, string fault) { path = destination; RunId = runId; TestFault = fault; ProcessId = Process.GetCurrentProcess().Id; StartedAtUtc = DateTime.UtcNow.ToString("o"); Phase = "foreground"; }
        public void Write() { ReportWrittenAtUtc = DateTime.UtcNow.ToString("o"); JsonFile.Write(path, this); }
        public void Fail(string error) { Error = error; }
        public void MarkFaultAfterReport() { ValidReportWrittenBeforeFault = true; }
        public void Complete(int exitCode) { ExitCode = exitCode; Completed = true; Write(); }
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
        private readonly string testFault;
        public CollectionLoop(string explicitTestFault = null) { testFault = explicitTestFault; thread = new Thread(Run) { IsBackground = true, Name = "NetPeek connection sampler" }; thread.Start(); }
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
                        try {
                            if (testFault == "capture-always") throw new InvalidOperationException("显式 smoke 测试注入：采集失败");
                            snapshot = testFault == "empty-snapshot" ? new NetworkSnapshot { IsComplete = true, CapturedAtUtc = DateTime.UtcNow } : collector.Capture();
                        }
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
