using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading;
internal static class NetPeekFixture
{
    public static int Main(string[] args)
    {
        if(args.Length!=2)return 2;
        var listener=new TcpListener(IPAddress.Loopback,0);
        try {
            listener.Start();
            File.WriteAllText(args[0],"{\"ProcessId\":"+Process.GetCurrentProcess().Id+",\"Port\":"+((IPEndPoint)listener.LocalEndpoint).Port+"}");
            var clock=Stopwatch.StartNew();
            while(!File.Exists(args[1]) && clock.Elapsed.TotalSeconds<240)Thread.Sleep(100);
            return 0;
        } finally {listener.Stop();}
    }
}
