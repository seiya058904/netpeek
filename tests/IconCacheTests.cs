using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetPeek;
using NetPeek.Core;

internal static class IconCacheTests
{
    private static int passed;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); passed++; Console.WriteLine("PASS " + text); }
    private static ImageSource Icon() { var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 50, 150, 70, 255 }, 4); image.Freeze(); return image; }
    private static EndpointRecord Row(DateTime start)
    {
        return new EndpointRecord { Key = "endpoint", Protocol = "TCP", AddressFamily = "IPv4", State = "ESTABLISHED", LocalAddress = "127.0.0.1", LocalPort = 50,
            RemoteAddress = "127.0.0.1", RemotePort = 51, Process = new ProcessIdentity { ProcessId = 7, StartTimeUtcTicks = start.Ticks, InstanceId = "7:" + start.Ticks,
                AppKey = "path:C:\\APP.EXE", Path = @"C:\app.exe", Name = "app" } };
    }
    private static NetworkSnapshot Snapshot(DateTime time, params EndpointRecord[] rows) { return new NetworkSnapshot { CapturedAtUtc = time, IsComplete = true, Endpoints = new List<EndpointRecord>(rows) }; }
    [STAThread]
    public static int Main()
    {
        try { ExpiredRowRebinds(); Eviction(); FailureRetries(); InFlightAndDispose(); Console.WriteLine("SUCCESS " + passed + " fake-clock/offline assertions; no live 60-second wait or native icon extraction"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }
    private static void ExpiredRowRebinds()
    {
        DateTime start = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc), now = start;
        int calls = 0; ImageSource image = Icon();
        using (var cache = new IconCache<ImageSource>(2, TimeSpan.FromSeconds(5), () => now, path => { calls++; return Task.FromResult(image); })) {
            var store = new ConnectionStore(); store.Apply(Snapshot(now, Row(start)));
            ApplicationView original = store.Applications[0]; original.Icon = cache.GetAsync(original.Path).Result;
            now = start.AddSeconds(61); store.Apply(Snapshot(now));
            Check(store.Applications.Count == 0, "application expires after 60-second history using fake clock");
            now = start.AddSeconds(62); store.Apply(Snapshot(now, Row(start)));
            ApplicationView recreated = store.Applications[0];
            Check(!Object.ReferenceEquals(original, recreated) && recreated.Icon == null, "returning path has a new row requiring icon binding");
            Check(cache.CanRequest(recreated.Path), "positive cache entry remains eligible for a recreated row");
            recreated.Icon = cache.GetAsync(recreated.Path).Result;
            Check(Object.ReferenceEquals(image, recreated.Icon) && calls == 1, "cached icon binds again after application disappearance without re-extraction");
        }
    }
    private static void Eviction()
    {
        DateTime now = DateTime.UtcNow; var calls = new Dictionary<string, int>();
        using (var cache = new IconCache<string>(2, TimeSpan.FromSeconds(5), () => now, path => { calls[path] = calls.ContainsKey(path) ? calls[path] + 1 : 1; return Task.FromResult(path); })) {
            cache.GetAsync("A").Wait(); cache.GetAsync("B").Wait(); cache.GetAsync("A").Wait(); cache.GetAsync("C").Wait();
            Check(cache.Count == 2 && calls["A"] == 1, "LRU cache remains bounded and retains recently accessed positive entry");
            Check(cache.GetAsync("B").Result == "B" && calls["B"] == 2 && cache.Count == 2, "evicted path is extracted again rather than suppressed by an attempted-path set");
        }
    }
    private static void FailureRetries()
    {
        DateTime now = DateTime.UtcNow; int calls = 0;
        using (var cache = new IconCache<string>(2, TimeSpan.FromSeconds(5), () => now, path => { calls++; if (calls == 1) throw new InvalidOperationException("injected extraction failure"); return Task.FromResult("icon"); })) {
            Check(cache.GetAsync("A").Result == null && !cache.CanRequest("A"), "failed extraction returns no icon and enters retry backoff");
            now = now.AddSeconds(4); Check(cache.GetAsync("A").Result == null && calls == 1, "repeated refresh during backoff does not retry extraction");
            now = now.AddSeconds(1); Check(cache.CanRequest("A") && cache.GetAsync("A").Result == "icon" && calls == 2, "failed path retries and recovers at the backoff boundary");
        }
    }
    private static void InFlightAndDispose()
    {
        var completion = new TaskCompletionSource<string>(); int calls = 0;
        var cache = new IconCache<string>(1, TimeSpan.FromSeconds(5), () => DateTime.UtcNow, path => { calls++; return completion.Task; });
        Task<string> first = cache.GetAsync("A"), second = cache.GetAsync("a");
        Check(Object.ReferenceEquals(first, second) && calls == 1 && !cache.CanRequest("A"), "case-insensitive concurrent requests share exactly one in-flight task");
        Check(cache.GetAsync("B").Result == null && calls == 1 && cache.Count == 1, "all-inflight cache neither exceeds capacity nor evicts running extraction");
        cache.Dispose();
        Check(first.Result == null && second.Result == null && cache.Count == 0, "dispose releases pending consumers and clears cached values");
        completion.SetResult("late icon");
        Check(!cache.CanRequest("A") && cache.GetAsync("A").Result == null && calls == 1, "late completion cannot repopulate disposed cache or start another extraction");
        cache.Dispose();
    }
}
