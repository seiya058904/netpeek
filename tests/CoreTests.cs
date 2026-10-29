using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetPeek.Core;

internal static class CoreTests
{
    private static int passed;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--child")
        {
            using (UdpClient child = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) { Console.WriteLine(((IPEndPoint)child.Client.LocalEndPoint).Port); Console.Out.Flush(); Console.ReadLine(); }
            return 0;
        }
        try
        {
            bool live = args.Contains("--live");
            bool interfaces = args.Contains("--interfaces");
            if (interfaces && !live) throw new ArgumentException("--interfaces requires --live");
            LayoutAndDecode(); Identity(); Failures();
            if (live)
            {
                Live(AddressFamily.InterNetwork);
                if (Socket.OSSupportsIPv6)
                {
                    Live(AddressFamily.InterNetworkV6);
                    if (interfaces) ScopedIpv6();
                }
                else Console.WriteLine("SKIP IPv6: Windows reports IPv6 unavailable");
                ExitedProcess();
            }
            else Console.WriteLine("SKIP live socket tests: offline is the default; opt in with --live");
            if (!interfaces) Console.WriteLine("SKIP interface socket tests: opt in with --live --interfaces; firewall prompts are possible");
            Console.WriteLine("SUCCESS " + passed + " assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }
    private static void LayoutAndDecode()
    {
        Check(Marshal.SizeOf(typeof(NativeTables.Tcp4Row)) == 24, "TCP4 native stride");
        Check(Marshal.SizeOf(typeof(NativeTables.Udp4Row)) == 12, "UDP4 native stride");
        Check(Marshal.SizeOf(typeof(NativeTables.Tcp6Row)) == 56, "TCP6 native stride");
        Check(Marshal.SizeOf(typeof(NativeTables.Udp6Row)) == 28, "UDP6 native stride");
        Check(NativeTables.DecodePort(0xBB01) == 443 && NativeTables.DecodePort(0xFFFF) == 65535 && NativeTables.DecodePort(0x0100) == 1, "network-order ports decode");
        NativeTables.Tcp6Row row = new NativeTables.Tcp6Row { LocalAddress = IPAddress.Parse("fe80::1234").GetAddressBytes(), LocalScope = 12, LocalPort = 0xBB01, RemoteAddress = IPAddress.IPv6Any.GetAddressBytes(), RemotePort = 0xFFFF, State = 2, Pid = 123 };
        IntPtr buffer = Marshal.AllocHGlobal(60);
        try
        {
            Marshal.WriteInt32(buffer, 1); Marshal.StructureToPtr(row, IntPtr.Add(buffer, 4), false);
            EndpointRecord decoded = NativeTables.Decode(buffer, 60, true, true)[0];
            Check(decoded.LocalAddress == "fe80::1234%12" && decoded.LocalPort == 443 && decoded.Process.ProcessId == 123, "IPv6 address and scope preserved");
            Check(decoded.State == "LISTEN" && decoded.RemoteAddress == null && decoded.RemotePort == 0, "listener ignores meaningless remote fields");
            Marshal.WriteInt32(buffer, 2);
            bool rejected = false; try { NativeTables.Decode(buffer, 60, true, true); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "truncated native table rejected");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void Identity()
    {
        ProcessIdentity a = NetworkCollector.BuildIdentity(5, 10, new ProcessObservation { StartTimeUtcTicks = 10, Path = @"C:\One\app.exe" }, "1");
        ProcessIdentity b = NetworkCollector.BuildIdentity(6, 20, new ProcessObservation { StartTimeUtcTicks = 20, Path = @"C:\Two\app.exe" }, "1");
        ProcessIdentity c = NetworkCollector.BuildIdentity(7, 30, new ProcessObservation { StartTimeUtcTicks = 30, Path = @"c:\one\APP.exe" }, "1");
        Check(a.Name == b.Name && a.AppKey != b.AppKey, "same executable name different paths stay separate");
        Check(a.AppKey == c.AppKey && a.InstanceId != c.InstanceId, "normalized same path groups multiple process instances");
        ProcessIdentity replaced = NetworkCollector.BuildIdentity(5, 10, new ProcessObservation { StartTimeUtcTicks = 99, Path = @"C:\Other\app.exe" }, "2");
        Check(replaced.Path == null && replaced.StartTimeUtcTicks == 0 && replaced.AppKey != a.AppKey, "PID reused during table capture becomes unknown");
        ProcessIdentity next = NetworkCollector.BuildIdentity(5, 99, new ProcessObservation { StartTimeUtcTicks = 99, Path = @"C:\Other\app.exe" }, "3");
        Check(next.InstanceId != a.InstanceId && next.Path.EndsWith(@"Other\app.exe"), "next capture resolves reused PID without stale metadata");
        ProcessIdentity unknown = NetworkCollector.BuildIdentity(8, 0, new ProcessObservation { Error = "denied" }, "1");
        ProcessIdentity unknownOther = NetworkCollector.BuildIdentity(9, 0, new ProcessObservation { Error = "denied" }, "1");
        Check(unknown.Path == null && unknown.AppKey != unknownOther.AppKey && unknown.AccessNote == "denied", "unknown processes never merge by display name");
        ProcessIdentity newProcess = NetworkCollector.BuildIdentity(8, 0, new ProcessObservation { StartTimeUtcTicks = 100, Path = @"C:\New\app.exe" }, "2");
        Check(newProcess.Path == null, "process first seen after table capture not falsely attributed");
        ProcessIdentity inaccessible = NetworkCollector.BuildIdentity(8, 100, new ProcessObservation { StartTimeUtcTicks = 100, Error = "path denied" }, "3");
        Check(inaccessible.InstanceId == "8:100" && inaccessible.Path == null, "verified instance with inaccessible path stays isolated");
    }
    private sealed class FakeTables : ITableSource
    {
        internal int FailCount;
        internal int Calls;
        internal bool IncludeRows;
        public List<EndpointRecord> Read(bool tcp, bool ipv6)
        {
            Calls++; if (Calls <= FailCount) throw new InvalidOperationException("injected table failure");
            List<EndpointRecord> result = new List<EndpointRecord>();
            if (IncludeRows) result.Add(new EndpointRecord { Protocol = tcp ? "TCP" : "UDP", AddressFamily = ipv6 ? "IPv6" : "IPv4", LocalAddress = "127.0.0.1", LocalPort = 1234, State = tcp ? "LISTEN" : "BOUND", Process = new ProcessIdentity { ProcessId = 999 } });
            return result;
        }
    }
    private sealed class FakeProcesses : IProcessSource
    {
        public Dictionary<int, long> CaptureStartTimes() { return new Dictionary<int, long>(); }
        public ProcessObservation Read(int pid) { return new ProcessObservation(); }
    }
    private static void Failures()
    {
        FakeTables source = new FakeTables { FailCount = 4 };
        using (NetworkCollector collector = new NetworkCollector(source, new FakeProcesses()))
        {
            NetworkSnapshot failed = collector.Capture();
            Check(!failed.IsComplete && failed.Warnings.Count == 4 && source.Calls == 4, "all four table failures reported distinctly from empty result");
            NetworkSnapshot recovered = collector.Capture();
            Check(recovered.IsComplete && recovered.Warnings.Count == 0 && recovered.Endpoints.Count == 0, "next capture recovers to complete empty snapshot");
        }
        using (NetworkCollector collector = new NetworkCollector(new FakeTables { FailCount = 1, IncludeRows = true }, new FakeProcesses()))
        {
            NetworkSnapshot partial = collector.Capture();
            Check(!partial.IsComplete && partial.Warnings.Count == 1 && partial.Endpoints.Count == 3, "single table failure preserves successful table rows");
        }
        NetworkCollector disposed = new NetworkCollector(); disposed.Dispose(); disposed.Dispose();
        bool threw = false; try { disposed.Capture(); } catch (ObjectDisposedException) { threw = true; }
        Check(threw, "dispose is idempotent and prevents later collection");
    }
    private static void Live(AddressFamily family)
    {
        IPAddress loopback = family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        string label = family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
        TcpListener listener = new TcpListener(loopback, 0); listener.Start();
        try
        {
            int listenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            using (TcpClient client = new TcpClient(family))
            using (UdpClient udp = new UdpClient(new IPEndPoint(loopback, 0)))
            using (NetworkCollector collector = new NetworkCollector())
            {
                client.Connect(loopback, listenPort);
                using (TcpClient accepted = listener.AcceptTcpClient())
                {
                    int pid = Process.GetCurrentProcess().Id;
                    int clientPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;
                    int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
                    NetworkSnapshot snapshot = collector.Capture();
                    Check(snapshot.IsComplete, label + " all native tables succeed: " + String.Join("; ", snapshot.Warnings));
                    List<EndpointRecord> own = snapshot.Endpoints.Where(e => e.Process.ProcessId == pid && e.AddressFamily == label).ToList();
                    Check(own.Any(e => e.Protocol == "TCP" && e.State == "LISTEN" && e.LocalPort == listenPort && e.RemoteAddress == null), label + " own TCP listener");
                    Check(own.Any(e => e.Protocol == "TCP" && e.State == "ESTABLISHED" && e.LocalPort == clientPort && e.RemotePort == listenPort && e.RemoteAddress == loopback.ToString()), label + " established TCP remote tuple");
                    Check(own.Any(e => e.Protocol == "UDP" && e.State == "BOUND" && e.LocalPort == udpPort && e.RemoteAddress == null), label + " UDP stays bound with unknown remote");
                    EndpointRecord endpoint = own.First(e => e.LocalPort == clientPort);
                    Check(endpoint.Process.StartTimeUtcTicks > 0 && !String.IsNullOrEmpty(endpoint.Process.Path), label + " current process verified by creation time and full path");
                    string identity = endpoint.Process.InstanceId;
                    Check(collector.Capture().Endpoints.Any(e => e.Key == endpoint.Key && e.Process.InstanceId == identity), label + " stable endpoint key across refresh");
                }
            }
        }
        finally { listener.Stop(); }
    }
    private static void ScopedIpv6()
    {
        List<IPAddress> addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(a => a.IsIPv6LinkLocal && a.ScopeId != 0).ToList();
        if (addresses.Count == 0) { Console.WriteLine("SKIP live IPv6 scope: no active link-local interface"); return; }
        foreach (IPAddress address in addresses)
        {
            using (UdpClient udp = new UdpClient(new IPEndPoint(address, 0)))
            using (NetworkCollector collector = new NetworkCollector())
            {
                int port = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
                Check(collector.Capture().Endpoints.Any(e => e.Protocol == "UDP" && e.LocalAddress == address.ToString() && e.LocalPort == port), "live link-local scope agrees with Windows adapter: " + address);
                TcpListener listener = new TcpListener(address, 0); listener.Start();
                try
                {
                    int tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                    Check(collector.Capture().Endpoints.Any(e => e.Protocol == "TCP" && e.LocalAddress == address.ToString() && e.LocalPort == tcpPort && e.State == "LISTEN"), "live TCP scoped listener: " + address);
                }
                finally { listener.Stop(); }
            }
        }
    }
    private static void ExitedProcess()
    {
        ProcessStartInfo start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--child") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        using (Process child = Process.Start(start))
        using (NetworkCollector collector = new NetworkCollector())
        {
            try
            {
                int port = Int32.Parse(child.StandardOutput.ReadLine());
                int pid = child.Id;
                NetworkSnapshot alive = collector.Capture();
                EndpointRecord childEndpoint = alive.Endpoints.FirstOrDefault(e => e.Protocol == "UDP" && e.LocalPort == port && e.Process.ProcessId == pid);
                Check(childEndpoint != null && childEndpoint.Process.StartTimeUtcTicks > 0, "live child UDP endpoint has verified ownership");
                child.StandardInput.WriteLine("exit"); child.StandardInput.Flush();
                Check(child.WaitForExit(5000), "child exits cooperatively");
                Check(!collector.Capture().Endpoints.Any(e => e.Protocol == "UDP" && e.LocalPort == port && e.Process.ProcessId == pid), "exited child endpoint removed on next capture");
                Check(new NativeProcesses().Read(pid).StartTimeUtcTicks == 0, "exited process metadata not reused");
            }
            finally { if (!child.HasExited) { child.StandardInput.Close(); child.WaitForExit(5000); } }
        }
    }
}
