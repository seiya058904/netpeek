using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Runs the unchanged real product entry point inside a test-owned process.
// No external UI Automation, synthetic application rows, or collector substitutions.
internal static class WpfHarness
{
    private static string target, destination, mode;
    private static int seconds, repetitions, fixturePid, phaseIndex, repetition;
    private static Window window;
    private static Dispatcher dispatcher;
    private static DispatcherTimer timer;
    private static bool started, busy, completed;
    private static bool fixtureSearchApplied;
    private static string failure;
    private static DateTime initialized;
    private static readonly Stopwatch lifetime = Stopwatch.StartNew();
    private static readonly List<object> runs = new List<object>();
    private static readonly List<object> expansion = new List<object>();
    private static readonly List<object> guiChecks = new List<object>();
    private static readonly List<object> invalidRuns = new List<object>();
    private static readonly List<object> windowStateEvents = new List<object>();
    private static DataGrid selectionGrid;
    private static object selectedEndpoint;
    private static ScrollViewer selectedScroll;
    private static double selectedOffset;
    private static DateTime selectedSampleTimestamp, selectionDeadline;
    private static int retainedSnapshots;
    private static IInputElement selectedFocus;
    private static bool selectionFocusInitiallyObserved, selectionFocusRetained;
    private static int measurementRetry;
    private static IntPtr ownWindowHandle;
    private static readonly string[] phases = { "overview", "expanded", "background" };
    private static object fixtureApplication;
    private static object ownWindowDisplay;
    private static int guiStage;
    private static string pausedText, expectedClipboard, iconFixturePath;
    private static object previousIconView;
    private static DateTime guiDeadline;
    private static bool retentionRestored;
    private static DateTime retentionRefreshDeadline;
    private sealed class OwnedFixture { internal Process Process; internal string StopPath; }
    private static readonly List<OwnedFixture> ownedFixtures=new List<OwnedFixture>();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct ProcessEntry {
        public uint Size, Usage, ProcessId; public IntPtr DefaultHeap; public uint ModuleId, Threads, ParentProcessId; public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Name;
    }
    private sealed class ProcessSample {
        public int Id; public long StartTicks; public double CpuSeconds; public long WorkingSetBytes, PrivateBytes;
    }
    private sealed class Sample {
        public string TimestampUtc; public long Tick; public List<ProcessSample> Processes; public bool AppIsForeground;
        public bool WindowVisible, WindowMinimized;
        public long WorkingSetBytes, PrivateBytes;
    }
    [STAThread] public static int Main(string[] args)
    {
        if (args.Length != 6) { Console.Error.WriteLine("WpfHarness TARGET REPORT MODE SECONDS RUNS FIXTURE_PID"); return 2; }
        target=Path.GetFullPath(args[0]); destination=Path.GetFullPath(args[1]); mode=args[2]; seconds=Int32.Parse(args[3]); repetitions=Int32.Parse(args[4]); fixturePid=Int32.Parse(args[5]);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        dispatcher=Dispatcher.CurrentDispatcher;
        timer=new DispatcherTimer(DispatcherPriority.Background) { Interval=TimeSpan.FromMilliseconds(200) };
        timer.Tick += Tick; timer.Start();
        int productExit=-1;
        try {
            var assembly=Assembly.LoadFrom(target);
            var entry=assembly.GetType("NetPeek.Program",true).GetMethod("Main",BindingFlags.Public|BindingFlags.Static);
            productExit=(int)entry.Invoke(null,new object[] { new string[0] });
            if (!completed && failure==null) failure="Product exited before test completion.";
        } catch (Exception error) { failure=error.ToString(); }
        finally {
            timer.Stop();
            foreach(var fixture in ownedFixtures) { try { File.WriteAllText(fixture.StopPath,"stop"); if(!fixture.Process.WaitForExit(5000)) fixture.Process.Kill(); fixture.Process.Dispose(); } catch {} }
            Write(destination,new {
                SchemaVersion=1,TargetExecutable=target,TargetSha256=Hash(target),HarnessSha256=Hash(Assembly.GetExecutingAssembly().Location),
                ProcessId=Process.GetCurrentProcess().Id,Mode=mode,SampleSeconds=seconds,RequestedRuns=repetitions,FixtureProcessId=fixturePid,
                LogicalProcessors=Environment.ProcessorCount,OSVersion=Environment.OSVersion.Version.ToString(),
                Success=completed && failure==null && productExit==0,ProductExitCode=productExit,Failure=failure,OwnWindowDisplay=ownWindowDisplay,
                Method="Unchanged real EXE Program.Main runs inside a common test-owned WPF process. Measurements include identical test-host and in-process sampler overhead; they are not standalone EXE footprints. Real endpoint table snapshots bracket samples. WPF bindings/control events manipulate only this host's actual UI. Expansion response is property assignment through UpdateLayout and dispatcher ContextIdle; it is not mouse-to-display latency.",
                Runs=runs,InvalidRuns=invalidRuns,Expansion=expansion,GuiChecks=guiChecks,WindowStateEvents=windowStateEvents,CompletedAtUtc=DateTime.UtcNow.ToString("o")
            });
        }
        return completed && failure==null && productExit==0 ? 0 : 1;
    }
    private static void Tick(object sender,EventArgs e)
    {
        try {
            if (lifetime.Elapsed.TotalSeconds>600) throw new TimeoutException("Harness exceeded 10 minutes.");
            if (busy || completed) return;
            if (!started) {
                if (Application.Current==null || Application.Current.MainWindow==null || !Application.Current.MainWindow.IsLoaded) return;
                window=Application.Current.MainWindow;
                ownWindowHandle=new System.Windows.Interop.WindowInteropHelper(window).Handle;
                window.StateChanged+=delegate {windowStateEvents.Add(new {TimestampUtc=DateTime.UtcNow.ToString("o"),State=window.WindowState.ToString(),Mode=mode,PhaseIndex=phaseIndex,Repetition=repetition,GuiStage=guiStage});};
                var list=(ListBox)window.FindName("AppList");
                if (list==null || list.Items.Count==0) return;
                fixtureApplication=list.Items.Cast<object>().FirstOrDefault(a => Items(Get(a,"Processes")).Any(p => Convert.ToInt32(Get(Get(p,"Identity"),"ProcessId"))==fixturePid));
                if (fixturePid>0 && fixtureApplication==null) return;
                window.Width=1060; window.Height=800; window.WindowState=WindowState.Normal; window.Activate();
                if(mode=="benchmark") window.Topmost=true;
                window.UpdateLayout();
                CaptureOwnDisplay();
                started=true; initialized=DateTime.UtcNow; return;
            }
            if ((DateTime.UtcNow-initialized).TotalSeconds<5) return;
            if(mode=="dpi") {busy=true;RunMinimumWindow();return;}
            if (mode=="gui") { busy=true; RunMinimumWindow(); return; }
            if (mode!="benchmark") throw new ArgumentException("Unknown harness mode.");
            busy=true;
            if (phaseIndex==1 && repetition==0 && expansion.Count==0) {
                if(fixturePid<=0) throw new InvalidOperationException("Controlled benchmark requires a real fixture PID.");
                window.WindowState=WindowState.Normal;window.Show();window.Topmost=true;
                if(!fixtureSearchApplied) {
                    ((TextBox)window.FindName("SearchBox")).Text=fixturePid.ToString();fixtureSearchApplied=true;
                    var debounce=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(450)};
                    debounce.Tick+=delegate {debounce.Stop();try {ExpandMeasured(0);}catch(Exception error){Fail(error);}};debounce.Start();
                } else ExpandMeasured(0);
                return;
            }
            if (phaseIndex==2 && repetition==0) { window.WindowState=WindowState.Minimized; }
            StartMeasurement();
        } catch(Exception error) { Fail(error); }
    }
    private static object Get(object value,string property) { return value.GetType().GetProperty(property).GetValue(value,null); }
    private static void CaptureOwnDisplay()
    {
        var scale=PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
        ownWindowDisplay=new {TransformToDeviceM11=scale.M11,TransformToDeviceM22=scale.M22,GetDpiForWindow=GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle),LogicalWindowWidth=window.ActualWidth,LogicalWindowHeight=window.ActualHeight,LogicalContentWidth=((FrameworkElement)window.Content).ActualWidth,LogicalContentHeight=((FrameworkElement)window.Content).ActualHeight,ImageMethod="Actual WPF content root rendered at 96 dpi in logical coordinates; not a physical desktop screenshot."};
    }
    private static void RunMinimumWindow()
    {
        window.WindowState=WindowState.Normal;window.Show();window.Topmost=true;window.Width=720;window.Height=480;
        ((TextBox)window.FindName("SearchBox")).Text=fixturePid.ToString();Click("PauseButton");Click("InfoButton");window.UpdateLayout();
        var settle=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(500)};
        settle.Tick+=delegate {settle.Stop();try {
            ExpandControlledFixture(true);CaptureOwnDisplay();
            var list=(ListBox)window.FindName("AppList");
            var grid=Visuals(window).OfType<DataGrid>().Single(g=>g.IsVisible && g.DataContext!=null && g.DataContext.GetType().GetProperty("Identity")!=null && Convert.ToInt32(Get(Get(g.DataContext,"Identity"),"ProcessId"))==fixturePid);
            var copy=Visuals(grid).OfType<Button>().First(b=>(string)b.Tag=="CopyEndpoint");
            Rect copyBounds=copy.TransformToAncestor(grid).TransformBounds(new Rect(0,0,copy.ActualWidth,copy.ActualHeight));
            bool copyReachable=copyBounds.Left>=-1 && copyBounds.Right<=grid.ActualWidth+1 && copy.IsEnabled;
            Check(((Border)window.FindName("WarningPanel")).Visibility==Visibility.Visible && ((Border)window.FindName("InfoPanel")).Visibility==Visibility.Visible,"Both auxiliary panels visible","Actual PauseButton and InfoButton routed events on the real 720 by 480 logical window.");
            Check(list.ActualHeight>=70,"Minimum-window connection viewport","Actual AppList layout height is at least 70 logical pixels.");
            Check(copyReachable,"Copy column horizontally reachable","Actual realized endpoint copy button bounds lie within the DataGrid width; this is a layout check, not a mouse-click test.");
            guiChecks.Add(new {Passed=true,Name="Minimum-window measured layout",Detail="Actual own-window WPF geometry at the current desktop DPI.",ListViewportHeight=list.ActualHeight,GridWidth=grid.ActualWidth,GridHeight=grid.ActualHeight,GridFrozenColumns=grid.FrozenColumnCount,CopyButtonLeft=copyBounds.Left,CopyButtonRight=copyBounds.Right,RealizedEndpointRows=Visuals(grid).OfType<DataGridRow>().Count()});
            SaveImage("minimum-window");
            if(mode=="dpi") {completed=true;Click("ExitButton");}
            else {
                Click("PauseButton");Click("InfoButton");((TextBox)window.FindName("SearchBox")).Text="";
                window.Width=1060;window.Height=800;window.Topmost=false;window.UpdateLayout();
                var resume=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(500)};
                resume.Tick+=delegate {resume.Stop();try {RunGui();}catch(Exception error){Fail(error);}};resume.Start();
            }
        }catch(Exception error){Fail(error);}};settle.Start();
    }
    private static void Set(object value,string property,object next) { value.GetType().GetProperty(property).SetValue(value,next,null); }
    private static IEnumerable<object> Items(object value) { return ((IEnumerable)value).Cast<object>(); }
    private static void ExpandAll(bool expand)
    {
        var list=(ListBox)window.FindName("AppList");
        foreach(var app in list.Items.Cast<object>()) {
            Set(app,"IsExpanded",expand);
            foreach(var process in Items(Get(app,"Processes"))) Set(process,"IsExpanded",expand);
        }
        if(expand && fixtureApplication!=null) list.ScrollIntoView(fixtureApplication);
        window.UpdateLayout();
    }
    private static void ExpandMeasured(int attempt)
    {
        var clock=Stopwatch.StartNew();
        ExpandControlledFixture(true);
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(delegate {
            try {
                var list=(ListBox)window.FindName("AppList");
                int gridCount=Visuals(window).OfType<DataGrid>().Count();
                int rows=Visuals(window).OfType<DataGridRow>().Count();
                var targetGrids=Visuals(window).OfType<DataGrid>().Where(g=>g.IsVisible && g.DataContext!=null && g.DataContext.GetType().GetProperty("Identity")!=null && Convert.ToInt32(Get(Get(g.DataContext,"Identity"),"ProcessId"))==fixturePid).ToList();
                int targetRows=targetGrids.Sum(g=>Visuals(g).OfType<DataGridRow>().Count());
                int currentEndpoints=Items(Get(Items(Get(fixtureApplication,"Processes")).Single(p=>Convert.ToInt32(Get(Get(p,"Identity"),"ProcessId"))==fixturePid),"Endpoints")).Count(x=>(bool)Get(x,"IsCurrent"));
                expansion.Add(new {Attempt=attempt+1,Milliseconds=clock.Elapsed.TotalMilliseconds,ExpandedApplications=list.Items.Count,RealizedDataGrids=gridCount,RealizedRows=rows,TargetVisibleDataGrids=targetGrids.Count,TargetRealizedRows=targetRows,TargetCurrentEndpointCount=currentEndpoints,Kind=attempt==0 ? "first expansion" : "warm re-expansion"});
                if(targetGrids.Count!=1 || targetRows==0 || currentEndpoints!=513) throw new InvalidOperationException("Controlled expansion must realize one actual 513-endpoint fixture grid.");
                if(attempt<2) {
                    ExpandControlledFixture(false);
                    dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(delegate { ExpandMeasured(attempt+1); }));
                } else { initialized=DateTime.UtcNow.AddSeconds(-3); busy=false; }
            } catch(Exception error) { Fail(error); }
        }));
    }
    private static void ExpandControlledFixture(bool expand)
    {
        var list=(ListBox)window.FindName("AppList");
        if(list.Items.Count!=1 || !list.Items.Cast<object>().Contains(fixtureApplication)) throw new InvalidOperationException("Fixture PID search did not isolate its application.");
        foreach(var app in list.Items.Cast<object>()) {
            Set(app,"IsExpanded",expand);
            foreach(var process in Items(Get(app,"Processes"))) Set(process,"IsExpanded",expand && Convert.ToInt32(Get(Get(process,"Identity"),"ProcessId"))==fixturePid);
        }
        list.ScrollIntoView(fixtureApplication);window.UpdateLayout();
    }
    private static void StartMeasurement()
    {
        string phase=phases[phaseIndex]; int run=repetition+1;
        Console.WriteLine(phase+" run "+run+"/"+repetitions);
        ThreadPool.QueueUserWorkItem(delegate {
            try {
                object before=CaptureSummary(phase+"_"+run+"_before");
                // Activate only our own WPF window after the windowless snapshot child exits.
                dispatcher.Invoke(new Action(delegate {
                    if(phase=="background") window.WindowState=WindowState.Minimized;
                    else {window.WindowState=WindowState.Normal;window.Show();window.Topmost=true;window.Activate();window.Focus();}
                }));
                Thread.Sleep(500);
                var samples=new List<Sample>(); samples.Add(Capture());
                var duration=Stopwatch.StartNew();
                while(duration.Elapsed.TotalSeconds<seconds) { Thread.Sleep(1000); samples.Add(Capture()); }
                object after=CaptureSummary(phase+"_"+run+"_after");
                double cpu=0;
                for(int i=1;i<samples.Count;i++) foreach(var p in samples[i].Processes) {
                    var previous=samples[i-1].Processes.FirstOrDefault(x=>x.Id==p.Id && x.StartTicks==p.StartTicks);
                    if(previous!=null) cpu+=Math.Max(0.0,p.CpuSeconds-previous.CpuSeconds);
                }
                double elapsed=(samples.Last().Tick-samples.First().Tick)/(double)Stopwatch.Frequency;
                bool validState=samples.All(x=>phase=="background" ? x.WindowMinimized : x.WindowVisible && !x.WindowMinimized);
                object measurement=new {Phase=phase,WindowStateValid=validState,ExpectedWindowState=phase=="background" ? "minimized" : "visible and not minimized",VisibleSamples=samples.Count(x=>x.WindowVisible),MinimizedSamples=samples.Count(x=>x.WindowMinimized),ElapsedSeconds=elapsed,CpuSeconds=cpu,CpuPercentOfMachine=100*cpu/elapsed/Environment.ProcessorCount,CpuPercentOfOneCore=100*cpu/elapsed,
                    AverageWorkingSetBytes=samples.Average(x=>(double)x.WorkingSetBytes),PeakWorkingSetBytes=samples.Max(x=>x.WorkingSetBytes),AveragePrivateBytes=samples.Average(x=>(double)x.PrivateBytes),PeakPrivateBytes=samples.Max(x=>x.PrivateBytes),ForegroundSamples=samples.Count(x=>x.AppIsForeground),SampleCount=samples.Count,Samples=samples};
                dispatcher.BeginInvoke(new Action(delegate {
                    if(!validState) {
                        invalidRuns.Add(new {Scenario=phase,Run=run,Attempt=measurementRetry+1,Before=before,After=after,Measurement=measurement,Reason="Actual own HWND state changed during sampling; excluded from scenario results."});
                        measurementRetry++;
                        if(measurementRetry>1) {Fail(new InvalidOperationException("Own window state was invalid twice for "+phase+" run "+run));return;}
                        busy=false;return;
                    }
                    runs.Add(new {Scenario=phase,Run=run,Before=before,After=after,Measurement=measurement});
                    measurementRetry=0;
                    repetition++;
                    if(repetition>=repetitions) { phaseIndex++; repetition=0; }
                    if(phaseIndex>=phases.Length) { completed=true; window.Close(); }
                    busy=false;
                }));
            } catch(Exception error) { dispatcher.BeginInvoke(new Action(delegate { Fail(error); })); }
        });
    }
    private static object CaptureSummary(string suffix)
    {
        string path=Path.Combine(Path.GetDirectoryName(destination),Path.GetFileNameWithoutExtension(destination)+"_"+suffix+"_snapshot.json");
        using(var process=Process.Start(new ProcessStartInfo(target,"--snapshot \""+path+"\"") {UseShellExecute=false,CreateNoWindow=true})) {
            if(!process.WaitForExit(30000)) {process.Kill();throw new TimeoutException("Snapshot child timeout.");}
            if(process.ExitCode!=0) throw new InvalidOperationException("Snapshot child failed.");
        }
        var json=new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue};
        var data=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
        if(!(bool)data["IsComplete"]) throw new InvalidOperationException("Incomplete network tables.");
        var endpoints=((IEnumerable)data["Endpoints"]).Cast<Dictionary<string,object>>().ToList();
        var processes=endpoints.Select(x=>(Dictionary<string,object>)x["Process"]).ToList();
        return new {RawSnapshot=path,CapturedAtUtc=data["CapturedAtUtc"],EndpointCount=endpoints.Count,ApplicationCount=processes.Select(x=>x["AppKey"]).Distinct().Count(),ProcessCount=processes.Select(x=>x["InstanceId"]).Distinct().Count(),FixtureEndpointCount=processes.Count(x=>Convert.ToInt32(x["ProcessId"])==fixturePid)};
    }
    private static Sample Capture()
    {
        int own=Process.GetCurrentProcess().Id;
        var ids=new HashSet<int> {own}; var relations=new List<KeyValuePair<int,int>>();
        IntPtr snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot==new IntPtr(-1)) throw new InvalidOperationException("Cannot enumerate process tree.");
        try {
            var entry=new ProcessEntry {Size=(uint)Marshal.SizeOf(typeof(ProcessEntry))};
            if(Process32First(snapshot,ref entry)) do {relations.Add(new KeyValuePair<int,int>((int)entry.ProcessId,(int)entry.ParentProcessId));} while(Process32Next(snapshot,ref entry));
        } finally {CloseHandle(snapshot);}
        bool changed; do {changed=false;foreach(var p in relations) if(ids.Contains(p.Value)&&ids.Add(p.Key)) changed=true;} while(changed);
        var rows=new List<ProcessSample>();
        foreach(int id in ids) try {using(var p=Process.GetProcessById(id)) rows.Add(new ProcessSample {Id=id,StartTicks=p.StartTime.ToUniversalTime().Ticks,CpuSeconds=p.TotalProcessorTime.TotalSeconds,WorkingSetBytes=p.WorkingSet64,PrivateBytes=p.PrivateMemorySize64});} catch {if(id==own) throw;}
        uint foreground; GetWindowThreadProcessId(GetForegroundWindow(),out foreground);
        return new Sample {TimestampUtc=DateTime.UtcNow.ToString("o"),Tick=Stopwatch.GetTimestamp(),Processes=rows,AppIsForeground=foreground==own,WindowVisible=IsWindowVisible(ownWindowHandle),WindowMinimized=IsIconic(ownWindowHandle),WorkingSetBytes=rows.Sum(x=>x.WorkingSetBytes),PrivateBytes=rows.Sum(x=>x.PrivateBytes)};
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var item in Visuals(VisualTreeHelper.GetChild(root,i))) yield return item;
    }
    private static void SaveImage(string name)
    {
        window.UpdateLayout();
        var content=(FrameworkElement)window.Content;content.UpdateLayout();
        var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
        var panel=content as Panel;var original=panel==null ? null : panel.Background;
        try {if(panel!=null && panel.Background==null)panel.Background=window.Background;content.UpdateLayout();image.Render(content);}
        finally {if(panel!=null)panel.Background=original;}
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        using(var stream=File.Create(Path.Combine(Path.GetDirectoryName(destination),name+".png"))) encoder.Save(stream);
    }
    private static void Check(bool success,string name,string detail)
    {
        guiChecks.Add(new {Passed=success,Name=name,Detail=detail});
        if(!success) throw new InvalidOperationException(name+": "+detail);
    }
    private static void RunGui()
    {
        guiStage=0; GuiStep();
    }
    private static void GuiDelay(int milliseconds,bool advance)
    {
        if(advance) guiStage++;
        var next=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        next.Tick+=delegate {next.Stop();try {GuiStep();}catch(Exception error){Fail(error);}}; next.Start();
    }
    private static void Click(string name) {var button=(Button)window.FindName(name);if(button==null)throw new InvalidOperationException("Button not found: "+name);button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,button));}
    private static void OwnKey(Key key) {var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,key) {RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(e);}
    private static object FindIconFixture() {return ((ListBox)window.FindName("AppList")).Items.Cast<object>().FirstOrDefault(x=>String.Equals((string)Get(x,"Path"),iconFixturePath,StringComparison.OrdinalIgnoreCase));}
    private static DateTime LatestUiEndpointTimestamp()
    {
        var current=((ListBox)window.FindName("AppList")).Items.Cast<object>().SelectMany(a=>Items(Get(a,"Processes"))).SelectMany(p=>Items(Get(p,"Endpoints"))).Where(x=>(bool)Get(x,"IsCurrent")).ToList();
        return current.Count==0 ? DateTime.MinValue : current.Max(x=>(DateTime)Get(x,"LastSeenUtc"));
    }
    private static object RetentionDiagnostic(object lingering)
    {
        string path=Path.Combine(Path.GetDirectoryName(destination),"retention_actual_snapshot.json");
        using(var capture=Process.Start(new ProcessStartInfo(target,"--snapshot \""+path+"\"") {UseShellExecute=false,CreateNoWindow=true})) {
            if(!capture.WaitForExit(30000)) {capture.Kill();throw new TimeoutException("Retention snapshot child timeout.");}
            if(capture.ExitCode!=0) throw new InvalidOperationException("Retention snapshot child failed.");
        }
        var raw=new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue}.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
        int oldPid=ownedFixtures[0].Process.Id;
        int actualCount=((IEnumerable)raw["Endpoints"]).Cast<Dictionary<string,object>>().Count(x=>Convert.ToInt32(((Dictionary<string,object>)x["Process"])["ProcessId"])==oldPid);
        var currentUiEndpoints=((ListBox)window.FindName("AppList")).Items.Cast<object>().SelectMany(a=>Items(Get(a,"Processes"))).SelectMany(p=>Items(Get(p,"Endpoints"))).Where(x=>(bool)Get(x,"IsCurrent")).ToList();
        return new {ObservedAtUtc=DateTime.UtcNow.ToString("o"),WindowVisible=window.IsVisible,WindowState=window.WindowState.ToString(),LastUpdated=((TextBlock)window.FindName("LastUpdated")).Text,Status=((TextBlock)window.FindName("StatusText")).Text,Warning=((TextBlock)window.FindName("WarningText")).Text,
            LatestUiCurrentEndpointTimestampUtc=currentUiEndpoints.Count==0 ? (DateTime?)null : currentUiEndpoints.Max(x=>(DateTime)Get(x,"LastSeenUtc")),
            OwnedFixtureHasExited=ownedFixtures[0].Process.HasExited,ActualSnapshotComplete=raw["IsComplete"],ActualSnapshotCapturedAtUtc=raw["CapturedAtUtc"],ActualFixtureEndpointCount=actualCount,RawSnapshot=path,
            LingeringEndpoints=Items(Get(lingering,"Processes")).SelectMany(p=>Items(Get(p,"Endpoints"))).Select(x=>new {Current=Get(x,"IsCurrent"),LastSeenUtc=Get(x,"LastSeenUtc")}).ToArray()};
    }
    private static OwnedFixture StartIconFixture()
    {
        iconFixturePath=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"NetPeekFixture.exe");
        string stem=Path.Combine(Path.GetDirectoryName(destination),"icon_fixture_"+ownedFixtures.Count);
        var fixture=new OwnedFixture {StopPath=stem+"_stop.txt"};
        if(File.Exists(fixture.StopPath)) throw new InvalidOperationException("Fixture stop file already exists; choose a fresh output directory.");
        fixture.Process=Process.Start(new ProcessStartInfo(iconFixturePath,"\""+stem+"_ready.json\" \""+fixture.StopPath+"\"") {UseShellExecute=false,CreateNoWindow=true});
        ownedFixtures.Add(fixture);return fixture;
    }
    private static void GuiStep()
    {
        var search=(TextBox)window.FindName("SearchBox");var list=(ListBox)window.FindName("AppList");
        switch(guiStage) {
            case 0:
                SaveImage("overview");search.Text=fixturePid.ToString();guiStage=100;GuiDelay(450,false);break;
            case 100:
                window.WindowState=WindowState.Normal;window.Show();window.Activate();
                ExpandControlledFixture(true);
                selectionGrid=Visuals(window).OfType<DataGrid>().Single(g=>g.IsVisible && g.DataContext!=null && g.DataContext.GetType().GetProperty("Identity")!=null && Convert.ToInt32(Get(Get(g.DataContext,"Identity"),"ProcessId"))==fixturePid);
                selectedEndpoint=selectionGrid.Items[Math.Min(50,selectionGrid.Items.Count-1)];selectionGrid.SelectedItem=selectedEndpoint;selectionGrid.ScrollIntoView(selectedEndpoint);window.UpdateLayout();GuiDelay(350,true);break;
            case 101:
                selectedScroll=Visuals(selectionGrid).OfType<ScrollViewer>().First();selectedOffset=selectedScroll.VerticalOffset;
                Check(selectedOffset>0,"Real endpoint detail scrolled","Actual selected fixture endpoint brought into view at a nonzero DataGrid vertical offset.");
                selectionGrid.Focus();uint focusPid;GetWindowThreadProcessId(GetForegroundWindow(),out focusPid);
                selectionFocusInitiallyObserved=selectionGrid.IsKeyboardFocusWithin && focusPid==Process.GetCurrentProcess().Id;selectionFocusRetained=selectionFocusInitiallyObserved;selectedFocus=Keyboard.FocusedElement;
                selectedSampleTimestamp=(DateTime)Get(selectedEndpoint,"LastSeenUtc");retainedSnapshots=0;selectionDeadline=DateTime.UtcNow.AddSeconds(12);GuiDelay(500,true);break;
            case 102:
                DateTime fresh=(DateTime)Get(selectedEndpoint,"LastSeenUtc");
                if(fresh>selectedSampleTimestamp) {
                    Check(Object.ReferenceEquals(selectionGrid.SelectedItem,selectedEndpoint),"Selection retained across real snapshot "+(retainedSnapshots+1),"The actual DataGrid SelectedItem keeps the same real fixture EndpointView object.");
                    Check(Math.Abs(selectedScroll.VerticalOffset-selectedOffset)<=0.01,"Scroll retained across real snapshot "+(retainedSnapshots+1),"Actual nonzero DataGrid VerticalOffset is unchanged after a newer real endpoint sample.");
                    uint retainedFocusPid;GetWindowThreadProcessId(GetForegroundWindow(),out retainedFocusPid);
                    if(selectionFocusRetained && selectionGrid.IsKeyboardFocusWithin && retainedFocusPid==Process.GetCurrentProcess().Id) Check(Object.ReferenceEquals(Keyboard.FocusedElement,selectedFocus),"Observed keyboard focus retained across real snapshot "+(retainedSnapshots+1),"Own window foreground and WPF keyboard focus were actually observed.");
                    else selectionFocusRetained=false;
                    selectedSampleTimestamp=fresh;retainedSnapshots++;
                }
                if(retainedSnapshots<3) {if(DateTime.UtcNow>=selectionDeadline)throw new TimeoutException("Fewer than three newer real UI samples while checking selection and scroll.");GuiDelay(500,false);break;}
                guiChecks.Add(new {Passed=selectionFocusRetained ? (bool?)true : null,Name="Keyboard focus retention observation",Detail="Physical focus retention is asserted only while both own-window foreground and WPF keyboard focus are actually observed; otherwise unverified.",FocusInitiallyObserved=selectionFocusInitiallyObserved,FocusObservedAcrossAllSnapshots=selectionFocusRetained,FreshSnapshots=retainedSnapshots,SelectedVerticalOffset=selectedOffset});
                search.Text="__netpeek_no_match_72c__";guiStage=1;GuiDelay(400,false);break;
            case 1:
                Check(list.Items.Count==0,"Search empty state","Real TextChanged and debounce path.");SaveImage("empty-search");
                OwnKey(Key.Escape);GuiDelay(400,true);break;
            case 2:
                Check(search.Text=="" && list.Items.Count>0,"Escape restores list","Own-window routed PreviewKeyDown; not external keyboard injection.");
                bool focusRequested=search.Focus();
                guiChecks.Add(new {Passed=(bool?)null,Name="Search keyboard focus observation",Detail="OS foreground focus is not guaranteed in this session; this observation is not a Ctrl+F input test.",FocusRequestAccepted=focusRequested,KeyboardFocusObserved=Keyboard.FocusedElement==search});
                search.Text="__netpeek_no_match_72c__";((ComboBox)window.FindName("FilterBox")).SelectedIndex=1;GuiDelay(400,true);break;
            case 3:
                var reset=window.FindName("ResetFiltersButton") as Button;
                if(reset!=null) {reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,reset));GuiDelay(400,true);}
                else {search.Text="";((ComboBox)window.FindName("FilterBox")).SelectedIndex=0;GuiDelay(400,true);}
                break;
            case 4:
                Check(search.Text=="" && ((ComboBox)window.FindName("FilterBox")).SelectedIndex==0 && list.Items.Count>0,"Reset filters restores all","Actual reset button when available; older version has no reset control.");
                ExpandAll(true);GuiDelay(400,true);break;
            case 5:
                Check(Visuals(window).OfType<DataGridRow>().Any(),"Real endpoint rows materialized","Real WPF binding/expansion path.");SaveImage("expanded");
                var copy=Visuals(window).OfType<Button>().FirstOrDefault(b=>(string)b.Tag=="CopyEndpoint" && b.DataContext!=null && Convert.ToInt32(Get(Get(Get(b.DataContext,"Record"),"Process"),"ProcessId"))==fixturePid);
                if(copy==null) throw new InvalidOperationException("No realized fixture copy button.");
                expectedClipboard=(string)Get(copy.DataContext,"CopyText");copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,copy));GuiDelay(1500,true);break;
            case 6:
                Check(Clipboard.GetText()==expectedClipboard,"Copy endpoint clipboard equality","Actual routed CopyEndpoint click; compared only the owned loopback endpoint value.");
                Click("PauseButton");pausedText=((TextBlock)window.FindName("LastUpdated")).Text;GuiDelay(2500,true);break;
            case 7:
                Check(((TextBlock)window.FindName("LastUpdated")).Text==pausedText,"Pause preserves timestamp","Waited 2.5 seconds with real collector still active.");Click("PauseButton");GuiDelay(3000,true);break;
            case 8:
                Check(((TextBlock)window.FindName("LastUpdated")).Text!=pausedText,"Resume updates timestamp","Real subsequent sample applied.");
                Click("TrayButton");GuiDelay(250,true);break;
            case 9:
                Check(!window.IsVisible,"Tray hide","Actual routed tray button click; same host process remains alive.");
                using(var second=Process.Start(new ProcessStartInfo(target){UseShellExecute=false,CreateNoWindow=true})) {if(!second.WaitForExit(5000)) {second.Kill();throw new InvalidOperationException("Second instance did not exit.");}Check(second.ExitCode==0,"Single-instance activation exit","Second real EXE sees product mutex and returns zero.");}
                GuiDelay(500,true);break;
            case 10:
                Check(window.IsVisible && Application.Current.MainWindow==window,"Single-instance restores same window","Product activation event calls real ShowWindow path.");
                StartIconFixture();guiDeadline=DateTime.UtcNow.AddSeconds(12);GuiDelay(1000,true);break;
            case 11:
                var initial=FindIconFixture();
                if(initial==null || Get(initial,"Icon")==null) {if(DateTime.UtcNow<guiDeadline){GuiDelay(500,false);break;}throw new InvalidOperationException("Fixture app/icon did not appear.");}
                previousIconView=initial;Check(true,"Unique real-icon fixture appears","Dedicated executable path; normal Windows executable icon loading.");
                File.WriteAllText(ownedFixtures[0].StopPath,"stop");Check(ownedFixtures[0].Process.WaitForExit(5000),"Icon fixture exits","Graceful owned fixture shutdown.");
                GuiDelay(65000,true);break;
            case 12:
                var lingering=FindIconFixture();
                if(lingering!=null) {
                    DateTime seen=Items(Get(lingering,"Processes")).SelectMany(p=>Items(Get(p,"Endpoints"))).Max(x=>(DateTime)Get(x,"LastSeenUtc"));
                    DateTime uiLatest=LatestUiEndpointTimestamp();
                    if(!retentionRestored && (window.WindowState==WindowState.Minimized || uiLatest<=seen.AddSeconds(60))) {
                        guiChecks.Add(new {Passed=true,Name="WindowRestoredForRetentionCheck",Detail="A minimized or stale UI intentionally skips painting. Restore the actual own window once, then wait at most ten seconds for a complete post-retention sample before asserting deletion.",PriorWindowState=window.WindowState.ToString(),PriorUiTimestampUtc=uiLatest,FixtureLastSeenUtc=seen});
                        retentionRestored=true;retentionRefreshDeadline=DateTime.UtcNow.AddSeconds(10);window.WindowState=WindowState.Normal;window.Show();window.Activate();GuiDelay(2000,false);break;
                    }
                    if(retentionRestored && uiLatest<=seen.AddSeconds(60) && DateTime.UtcNow<retentionRefreshDeadline) {GuiDelay(500,false);break;}
                }
                if(lingering!=null) guiChecks.Add(new {Passed=(bool?)null,Name="History retention diagnostic",Detail="Actual lingering fixture state after 65 seconds; diagnostic only.",State=RetentionDiagnostic(lingering)});
                Check(FindIconFixture()==null,"App disappears after history retention","Waited 65 seconds after dedicated app exited; no other process uses its path.");
                var restarted=StartIconFixture();Check(restarted.Process.Id!=ownedFixtures[0].Process.Id,"Fixture restarts with new PID","Same executable path, new actual process.");guiDeadline=DateTime.UtcNow.AddSeconds(12);GuiDelay(1000,true);break;
            case 13:
                var returned=FindIconFixture();
                if(returned==null || Get(returned,"Icon")==null) {if(DateTime.UtcNow<guiDeadline){GuiDelay(500,false);break;}throw new InvalidOperationException("Reappeared fixture lacks icon.");}
                Check(!Object.ReferenceEquals(previousIconView,returned),"Reappeared app gets icon on new view","New ApplicationView after 65-second removal has a real non-null icon.");
                for(int i=0;i<7;i++) StartIconFixture();guiDeadline=DateTime.UtcNow.AddSeconds(12);GuiDelay(1500,true);break;
            case 14:
                var multiple=FindIconFixture();int count=multiple==null ? 0 : Items(Get(multiple,"Processes")).Count();
                if(count<8 && DateTime.UtcNow<guiDeadline) {GuiDelay(500,false);break;}
                Check(count==8,"Eight-process actual endpoint group","Eight real same-path child processes, each with one loopback listener.");
                Set(multiple,"IsExpanded",true);foreach(var p in Items(Get(multiple,"Processes")))Set(p,"IsExpanded",true);list.ScrollIntoView(multiple);window.UpdateLayout();GuiDelay(400,true);break;
            case 15:
                SaveImage("multi-process");Check(Visuals(window).OfType<DataGrid>().Count()>0,"Multi-process details render","Real small multi-process fixture; no 257-process cache-pressure simulation.");
                completed=true;Click("ExitButton");break;
        }
    }
    private static void Fail(Exception error)
    {
        failure=error.ToString();busy=true;Console.Error.WriteLine(failure);
        if(window!=null) window.Close(); else if(Application.Current!=null) Application.Current.Shutdown(1);
    }
    private static string Hash(string path) {using(var hash=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");}
    private static void Write(string path,object value) {File.WriteAllText(path,new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue,RecursionLimit=96}.Serialize(value),new System.Text.UTF8Encoding(false));}
}
