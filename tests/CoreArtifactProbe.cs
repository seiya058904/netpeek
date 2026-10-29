using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using NetPeek.Core;

internal static class CoreArtifactProbe
{
    private static readonly Assembly Assembly = typeof(NetworkCollector).Assembly;
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags Methods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int passed;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); passed++; Console.WriteLine("PASS " + text); }
    private static Type Type(string name) { return Assembly.GetType("NetPeek.Core." + name, true); }
    private static void Field(object target, string name, object value) { target.GetType().GetField(name, Fields).SetValue(target, value); }
    private static object Invoke(string type, string method, params object[] args) { return Type(type).GetMethod(method, Methods).Invoke(null, args); }
    public static int Main()
    {
        foreach (var pair in new[] { new {Name="Tcp4Row", Size=24}, new {Name="Udp4Row", Size=12}, new {Name="Tcp6Row", Size=56}, new {Name="Udp6Row", Size=28} })
            Check(Marshal.SizeOf(Type("NativeTables+" + pair.Name)) == pair.Size, pair.Name + " native layout");
        Check((int)Invoke("NativeTables", "DecodePort", (uint)0xBB01) == 443 && (int)Invoke("NativeTables", "DecodePort", (uint)0xFFFF) == 65535, "final decoder preserves network-order ports");
        object row = Activator.CreateInstance(Type("NativeTables+Tcp6Row"));
        Field(row,"LocalAddress",IPAddress.Parse("fe80::1234").GetAddressBytes()); Field(row,"LocalScope",(uint)12); Field(row,"LocalPort",(uint)0xBB01);
        Field(row,"RemoteAddress",IPAddress.IPv6Any.GetAddressBytes()); Field(row,"RemoteScope",(uint)0); Field(row,"RemotePort",(uint)0xFFFF); Field(row,"State",(uint)2); Field(row,"Pid",(uint)123);
        IntPtr buffer = Marshal.AllocHGlobal(60);
        try {
            Marshal.WriteInt32(buffer,1); Marshal.StructureToPtr(row,IntPtr.Add(buffer,4),false);
            var result = (List<EndpointRecord>)Invoke("NativeTables","Decode",buffer,60,true,true);
            Check(result.Count == 1 && result[0].LocalAddress == "fe80::1234%12" && result[0].LocalPort == 443 && result[0].Process.ProcessId == 123, "final IPv6 decoder preserves address scope and owner PID");
            Check(result[0].State == "LISTEN" && result[0].RemoteAddress == null && result[0].RemotePort == 0, "final listener has no invented remote endpoint");
            Marshal.WriteInt32(buffer,2); bool rejected=false;
            try { Invoke("NativeTables","Decode",buffer,60,true,true); } catch(TargetInvocationException ex) { rejected=ex.InnerException is InvalidOperationException; }
            Check(rejected,"final decoder rejects truncated native buffers");
        } finally { Marshal.FreeHGlobal(buffer); }
        ProcessIdentity a=Identity(5,10,10,@"C:\One\app.exe","1"), b=Identity(6,20,20,@"C:\Two\app.exe","1"), same=Identity(7,30,30,@"c:\one\APP.exe","1");
        Check(a.Name==b.Name && a.AppKey!=b.AppKey && a.AppKey==same.AppKey,"final identity groups reliable paths and separates identical names at different paths");
        ProcessIdentity reused=Identity(5,10,99,@"C:\Other\app.exe","2"), replacement=Identity(5,99,99,@"C:\Other\app.exe","3");
        Check(reused.Path==null && reused.StartTimeUtcTicks==0 && replacement.InstanceId!=a.InstanceId,"final identity rejects PID-reuse races");
        ProcessIdentity unknown1=Identity(8,0,0,null,"1"), unknown2=Identity(8,0,0,null,"2");
        Check(unknown1.InstanceId!=unknown2.InstanceId && unknown1.Path==null && unknown1.AccessNote.Contains("身份不可核验"),"final unknown identities retain explicit uncertainty and separate epochs");
        Check(Identity(8,0,100,@"C:\New\app.exe","1").Path==null,"final newly-started process is not falsely attributed");
        Check(Identity(8,100,100,null,"1").InstanceId=="8:100","final inaccessible path retains verified process-instance grouping");
        Check((string)Invoke("NativeProcesses","NormalizeImagePath",@"\\?\UNC\server\share\Folder\..\app.exe")==@"\\server\share\app.exe","final UNC normalization is lexical and canonical");
        foreach(string path in new[]{@"C:app.exe",@"\Folder\app.exe",@"relative\app.exe",@"\\.\pipe\something",@"C:\"})
            Check(Identity(11,200,200,path,"1").Path==null,"final identity rejects ambiguous/non-executable path: "+path);
        var collector=new NetworkCollector(); collector.Dispose(); collector.Dispose(); bool disposed=false;
        try { collector.Capture(); } catch(ObjectDisposedException) { disposed=true; }
        Check(disposed,"final collector disposal prevents further capture");
        Console.WriteLine("SUCCESS "+passed+" final-EXE offline reflection assertions. Source mock failure/recovery tests remain separate source-unit coverage."); return 0;
    }
    private static ProcessIdentity Identity(int pid,long before,long after,string path,string epoch)
    {
        object observation=Activator.CreateInstance(Type("ProcessObservation"),true); Field(observation,"StartTimeUtcTicks",after); Field(observation,"Path",path);
        return (ProcessIdentity)Invoke("NetworkCollector","BuildIdentity",pid,before,observation,epoch);
    }
}
